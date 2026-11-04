using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using EasyTerminal.Core.Models;

namespace EasyTerminal.Core.Ssh;

/// <summary>A host with an optional port written into the address itself.</summary>
public readonly record struct HostEndpoint(string Host, int? Port)
{
    public override string ToString() => Port is { } port ? (Host.Contains(':') ? $"[{Host}]:{port}" : $"{Host}:{port}") : Host;

    /// <summary>Accepts host, host:port, [ipv6]:port and bare IPv6.</summary>
    public static HostEndpoint Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close > 0)
            {
                string host = text[1..close];
                return int.TryParse(text.AsSpan(close + 1).TrimStart(':'), out int v6Port) ? new HostEndpoint(host, v6Port) : new HostEndpoint(host, null);
            }
        }
        int colon = text.IndexOf(':');
        if (colon > 0 && colon == text.LastIndexOf(':') && int.TryParse(text.AsSpan(colon + 1), out int port))
            return new HostEndpoint(text[..colon], port);
        return new HostEndpoint(text, null);
    }

    public static IReadOnlyList<HostEndpoint> AllFor(Device device)
    {
        var all = new List<HostEndpoint> { Parse(device.Host) };
        foreach (var address in device.AlternateAddresses)
            if (!string.IsNullOrWhiteSpace(address)) all.Add(Parse(address));
        return all;
    }
}

public enum ProbeOutcome { SshServer, OpenButNotSsh, NotOnThisNetwork, NameNotResolved, Unreachable }

/// <summary>
/// Picks the first address of a device that answers. Private addresses the routing table can only send
/// to the default gateway are rejected without touching the network, so being away from home costs nothing.
/// </summary>
public sealed class AddressResolver
{
    private readonly Func<IPAddress, bool> _hasRoute;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _bannerTimeout;
    private readonly Func<string?, string?> _getSecret;

    public AddressResolver(Func<string?, string?> getSecret, Func<IPAddress, bool>? hasRoute = null, TimeSpan? connectTimeout = null, TimeSpan? bannerTimeout = null)
    {
        _getSecret = getSecret;
        _hasRoute = hasRoute ?? RouteTable.HasSpecificRoute;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2.5);
        _bannerTimeout = bannerTimeout ?? TimeSpan.FromSeconds(2.5);
    }

    /// <summary>
    /// Tries the addresses in order and returns the first SSH server. A port that is open but silent is
    /// only used if nothing better turns up, because a proxy or TUN adapter may accept any connection.
    /// When everything fails ssh still runs once, so it reports the real error.
    /// </summary>
    public async Task<HostEndpoint> ChooseAsync(Device device, int? portOverride, NetworkProfile? proxy, Action<string>? progress, CancellationToken cancellation)
    {
        var candidates = HostEndpoint.AllFor(device);
        if (candidates.Count == 1) return candidates[0];

        HostEndpoint? openButSilent = null;
        HostEndpoint? attempted = null;
        foreach (var candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            int port = portOverride ?? candidate.Port ?? device.Port;
            progress?.Invoke($"正在尝试 {candidate.Host}:{port}…");
            var outcome = await ProbeAsync(candidate.Host, port, proxy, cancellation);
            switch (outcome)
            {
                case ProbeOutcome.SshServer:
                    progress?.Invoke($"使用 {candidate.Host}:{port}");
                    return candidate;
                case ProbeOutcome.OpenButNotSsh:
                    openButSilent ??= candidate;
                    progress?.Invoke("  端口已打开，但没有收到 SSH 响应");
                    break;
                case ProbeOutcome.NotOnThisNetwork:
                    progress?.Invoke("  不在当前网络内，跳过");
                    break;
                case ProbeOutcome.NameNotResolved:
                    progress?.Invoke("  无法解析主机名，跳过");
                    break;
                default:
                    attempted ??= candidate;
                    progress?.Invoke("  无法连接，尝试下一个");
                    break;
            }
        }

        if (openButSilent is { } fallback)
        {
            progress?.Invoke($"使用 {fallback}（未确认是 SSH 服务）");
            return fallback;
        }
        // Prefer one that could at least be dialled, so ssh's error describes a real attempt.
        var last = attempted ?? candidates[0];
        progress?.Invoke($"所有地址都不可达，仍尝试 {last}");
        return last;
    }

    public async Task<ProbeOutcome> ProbeAsync(string host, int port, NetworkProfile? proxy, CancellationToken cancellation)
    {
        if (proxy is { ProxyKind: not ProxyKind.None })
            return await ProbeOnceAsync(cancellation, _connectTimeout * 2, async (client, token) =>
            {
                var route = ProxyRoute.Resolve(proxy, _getSecret);
                await client.ConnectAsync(route.Host, route.Port, token);
                var stream = client.GetStream();
                await Task.Run(() => ProxyHandshake.Perform(stream, route.Kind, host, port, route.User, route.Password), token);
            });

        IPAddress[] addresses;
        using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            lookup.CancelAfter(_connectTimeout);
            try
            {
                addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, lookup.Token);
            }
            catch (SocketException)
            {
                return ProbeOutcome.NameNotResolved;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return ProbeOutcome.NameNotResolved;
            }
        }
        if (addresses.Length == 0) return ProbeOutcome.NameNotResolved;

        var routable = addresses.Where(a => !IsPrivate(a) || _hasRoute(a)).ToArray();
        if (routable.Length == 0) return ProbeOutcome.NotOnThisNetwork;

        // Each IP gets its own timeout, so a dead IPv6 path cannot use up the time meant for IPv4.
        var best = ProbeOutcome.Unreachable;
        foreach (var address in routable)
        {
            var outcome = await ProbeOnceAsync(cancellation, _connectTimeout, (client, token) => client.ConnectAsync(address, port, token).AsTask());
            if (outcome == ProbeOutcome.SshServer) return outcome;
            if (outcome == ProbeOutcome.OpenButNotSsh) best = outcome;
        }
        return best;
    }

    private async Task<ProbeOutcome> ProbeOnceAsync(CancellationToken cancellation, TimeSpan connectTimeout, Func<TcpClient, CancellationToken, Task> connect)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var client = new TcpClient { NoDelay = true };
        // Cancelling a blocking handshake needs the socket closed underneath it.
        using var closeOnCancel = linked.Token.Register(client.Dispose);
        try
        {
            linked.CancelAfter(connectTimeout);
            await connect(client, linked.Token);
            linked.CancelAfter(Timeout.InfiniteTimeSpan);
            return await ReadBannerAsync(client.GetStream(), cancellation);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return ProbeOutcome.Unreachable;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            cancellation.ThrowIfCancellationRequested();
            return ProbeOutcome.Unreachable;
        }
    }

    /// <summary>RFC 4253 lets a server send lines before its identification, so any line may start it.</summary>
    private async Task<ProbeOutcome> ReadBannerAsync(NetworkStream stream, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(_bannerTimeout);
        var received = new StringBuilder();
        var buffer = new byte[512];
        try
        {
            while (received.Length < 4096)
            {
                int read = await stream.ReadAsync(buffer, linked.Token);
                // Accepting and hanging up is what TUN and transparent proxies do for an unreachable target.
                if (read == 0) return received.Length == 0 ? ProbeOutcome.Unreachable : ProbeOutcome.OpenButNotSsh;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                string text = received.ToString();
                if (text.StartsWith("SSH-", StringComparison.Ordinal) || text.Contains("\nSSH-", StringComparison.Ordinal)) return ProbeOutcome.SshServer;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            if (received.Length == 0) return ProbeOutcome.Unreachable;
        }
        return ProbeOutcome.OpenButNotSsh;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}

public static class RouteTable
{
    /// <summary>
    /// False when the only way to this IPv4 address is the default route, which for a private address
    /// means it is not on this network. A VPN or a routed LAN adds a specific route and so still passes.
    /// </summary>
    public static bool HasSpecificRoute(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return true;
        uint destination = BitConverter.ToUInt32(address.GetAddressBytes());
        // If the routing table cannot be read, let the connection attempt decide.
        return GetBestRoute(destination, 0, out var row) != 0 || row.Mask != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ForwardRow
    {
        public uint Destination, Mask, Policy, NextHop, InterfaceIndex, Type, Protocol, Age, NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestRoute(uint destination, uint source, out ForwardRow row);
}
