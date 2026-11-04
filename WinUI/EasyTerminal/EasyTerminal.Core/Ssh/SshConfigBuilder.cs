using System.Text;
using EasyTerminal.Core.Models;

namespace EasyTerminal.Core.Ssh;

public sealed record SshPlan(string Alias, string ConfigText, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Turns a device plus its per-connection overrides into a throwaway ssh_config. Using a generated file
/// instead of command-line flags lets each jump host carry its own user, key and proxy.
/// </summary>
public sealed class SshConfigBuilder
{
    private readonly Func<Guid, Device?> _findDevice;
    private readonly Func<string?, string?> _getSecret;
    private readonly string _helperPath;
    private readonly string? _userConfigPath;
    private readonly StringBuilder _config = new();
    private readonly Dictionary<string, string> _environment = new();
    private IReadOnlyDictionary<Guid, HostEndpoint>? _chosen;
    private int _nextHop;

    public SshConfigBuilder(Func<Guid, Device?> findDevice, Func<string?, string?> getSecret, string helperPath, string? userConfigPath)
    {
        _findDevice = findDevice;
        _getSecret = getSecret;
        _helperPath = helperPath;
        _userConfigPath = userConfigPath;
    }

    public SshPlan Build(Device device, Connection? connection, IReadOnlyDictionary<Guid, HostEndpoint>? chosenAddresses = null)
    {
        _chosen = chosenAddresses;
        _config.Clear();
        _environment.Clear();
        _nextHop = 0;

        var network = connection is { OverrideNetwork: true } ? connection.Network : device.Network;
        string user = !string.IsNullOrWhiteSpace(connection?.User) ? connection!.User : device.User;
        const string alias = "et-target";

        EmitHost(alias, device, user, connection?.Port, network, new HashSet<Guid> { device.Id });

        if (_userConfigPath != null)
        {
            // Host blocks win over the user's config because ssh keeps the first value it sees.
            _config.Append("Match all\n");
            _config.Append("  Include \"").Append(_userConfigPath.Replace('\\', '/')).Append("\"\n");
        }
        return new SshPlan(alias, _config.ToString(), new Dictionary<string, string>(_environment));
    }

    private void EmitHost(string alias, Device device, string user, int? portOverride, NetworkProfile network, HashSet<Guid> visited)
    {
        string? previousHop = EmitJumpChain(network, visited);

        _config.Append("Host ").Append(alias).Append('\n');
        var endpoint = _chosen != null && _chosen.TryGetValue(device.Id, out var chosen) ? chosen : HostEndpoint.Parse(device.Host);
        _config.Append("  HostName ").Append(endpoint.Host).Append('\n');
        _config.Append("  Port ").Append(portOverride ?? endpoint.Port ?? device.Port).Append('\n');
        // All addresses are one machine: a single known_hosts identity avoids re-prompting per address,
        // and flags a different server answering on a new network.
        if (device.AlternateAddresses.Count > 0) _config.Append("  HostKeyAlias ").Append(HostEndpoint.Parse(device.Host).Host).Append('\n');
        if (!string.IsNullOrWhiteSpace(user)) _config.Append("  User ").Append(user).Append('\n');

        if (device.Auth == AuthMethod.KeyFile && !string.IsNullOrWhiteSpace(device.KeyPath))
        {
            _config.Append("  IdentityFile \"").Append(device.KeyPath.Replace('\\', '/')).Append("\"\n");
            _config.Append("  IdentitiesOnly yes\n");
        }
        else if (device.Auth == AuthMethod.Password)
        {
            _config.Append("  PreferredAuthentications password,keyboard-interactive\n");
            _config.Append("  PubkeyAuthentication no\n");
        }

        if (device.ForwardAgent) _config.Append("  ForwardAgent yes\n");
        _config.Append("  ServerAliveInterval 30\n");
        _config.Append("  ServerAliveCountMax 4\n");

        if (previousHop != null)
            _config.Append("  ProxyJump ").Append(previousHop).Append('\n');
        else if (network.ProxyKind != ProxyKind.None)
            _config.Append("  ProxyCommand ").Append(ProxyCommand(network)).Append('\n');
    }

    /// <summary>Emits each hop, chained so the first hop uses the proxy and later hops tunnel through it.</summary>
    private string? EmitJumpChain(NetworkProfile network, HashSet<Guid> visited)
    {
        string? previous = null;
        foreach (var hopId in network.JumpDeviceIds)
        {
            var hop = _findDevice(hopId);
            if (hop == null) throw new InvalidOperationException("跳板设备已不存在，请检查网络配置。");
            if (!visited.Add(hopId)) throw new InvalidOperationException($"跳板链存在循环：{hop.Name}");

            string alias = $"et-hop{_nextHop++}";
            NetworkProfile hopNetwork;
            if (previous == null)
            {
                // The outermost hop dials out itself, so it uses this connection's proxy or else its own settings.
                hopNetwork = network.ProxyKind != ProxyKind.None
                    ? new NetworkProfile { ProxyKind = network.ProxyKind, ProxyHost = network.ProxyHost, ProxyPort = network.ProxyPort, ProxyUser = network.ProxyUser, ProxySecretId = network.ProxySecretId }
                    : hop.Network;
            }
            else
            {
                hopNetwork = new NetworkProfile();
            }

            string? inner = previous;
            if (inner != null)
            {
                EmitHostWithParent(alias, hop, inner);
            }
            else
            {
                EmitHost(alias, hop, hop.User, null, hopNetwork, visited);
            }
            previous = alias;
        }
        return previous;
    }

    private void EmitHostWithParent(string alias, Device hop, string parentAlias)
    {
        EmitHost(alias, hop, hop.User, null, new NetworkProfile(), new HashSet<Guid> { hop.Id });
        _config.Append("  ProxyJump ").Append(parentAlias).Append('\n');
    }

    private string ProxyCommand(NetworkProfile network)
    {
        // The built-in Tailscale node resolves to its loopback SOCKS5 port here.
        var route = ProxyRoute.Resolve(network, _getSecret);
        var sb = new StringBuilder();
        sb.Append('"').Append(_helperPath.Replace('\\', '/')).Append("\" --proxy-connect ");
        sb.Append(route.Kind).Append(' ').Append(route.Host).Append(' ').Append(route.Port).Append(" %h %p");
        if (!string.IsNullOrEmpty(route.User))
        {
            sb.Append(' ').Append(route.User);
            if (route.Password != null) _environment[SshHelperEntry.ProxyPasswordVariable(route.Host, route.Port)] = route.Password;
        }
        return sb.ToString();
    }
}

public static class ShellQuote
{
    /// <summary>Quotes a value for a POSIX shell on the remote side.</summary>
    public static string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
