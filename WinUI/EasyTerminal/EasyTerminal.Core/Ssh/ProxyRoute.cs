using System.Net;
using System.Net.Sockets;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Tailscale;

namespace EasyTerminal.Core.Ssh;

/// <summary>The proxy a network profile dials through, with the built-in Tailscale node resolved to its SOCKS5 port.</summary>
public readonly record struct ProxyRoute(string Kind, string Host, int Port, string? User, string? Password)
{
    public static ProxyRoute Resolve(NetworkProfile network, Func<string?, string?> getSecret)
    {
        if (network.ProxyKind == ProxyKind.Tailscale)
        {
            var proxy = TailscaleNode.Shared.Proxy;
            return new ProxyRoute("socks5", proxy.Host, proxy.Port, proxy.User, proxy.Password);
        }
        return new ProxyRoute(network.ProxyKind.ToString().ToLowerInvariant(), network.ProxyHost, network.ProxyPort,
            network.ProxyUser.Length > 0 ? network.ProxyUser : null, getSecret(network.ProxySecretId));
    }

    /// <summary>True when this profile, or any jump device it goes through, uses the built-in Tailscale node.</summary>
    public static bool UsesTailscale(NetworkProfile network, Func<Guid, Device?> findDevice)
    {
        var seen = new HashSet<Guid>();
        var pending = new Stack<NetworkProfile>();
        pending.Push(network);
        while (pending.TryPop(out var current))
        {
            if (current.ProxyKind == ProxyKind.Tailscale) return true;
            foreach (var id in current.JumpDeviceIds)
                if (seen.Add(id) && findDevice(id) is { } hop) pending.Push(hop.Network);
        }
        return false;
    }

    public async Task<NetworkStream> ConnectAsync(string targetHost, int targetPort, CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(Host, Port, cancellation);
            var stream = client.GetStream();
            var route = this;
            await Task.Run(() => ProxyHandshake.Perform(stream, route.Kind, targetHost, targetPort, route.User, route.Password), cancellation);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Listens on a loopback port and carries each connection through a proxy to one target. Lets clients that
/// cannot speak SOCKS themselves, such as the RDP control, reach hosts behind a proxy or in the tailnet.
/// </summary>
public sealed class ProxyForwarder : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ProxyRoute _route;
    private readonly string _targetHost;
    private readonly int _targetPort;

    public int LocalPort { get; }

    public ProxyForwarder(ProxyRoute route, string targetHost, int targetPort)
    {
        _route = route;
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient local;
            try
            {
                local = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = PumpAsync(local);
        }
    }

    private async Task PumpAsync(TcpClient local)
    {
        using (local)
        {
            local.NoDelay = true;
            try
            {
                await using var remote = await _route.ConnectAsync(_targetHost, _targetPort, _stop.Token);
                var localStream = local.GetStream();
                var up = CopyThenShutdownAsync(localStream, remote, remote.Socket);
                var down = CopyThenShutdownAsync(remote, localStream, local.Client);
                await Task.WhenAll(up, down);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
                // The client sees the connection drop; RDP reports it in its own words.
            }
        }
    }

    private async Task CopyThenShutdownAsync(Stream from, Stream to, Socket toSocket)
    {
        try
        {
            await from.CopyToAsync(to, 64 * 1024, _stop.Token);
        }
        finally
        {
            try
            {
                toSocket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
