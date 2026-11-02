using EasyTerminal.Controls;
using EasyTerminal.Core.Local;
using EasyTerminal.Core.Integrations;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Mosh;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Transport;
using Microsoft.UI.Dispatching;

namespace EasyTerminal.Services;

/// <summary>App-wide state shared by the window and its pages. Events fire on the UI thread.</summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    public AppStore Store { get; }
    public SshRunner Ssh { get; }
    public DispatcherQueue Dispatcher { get; }
    public string HelperPath { get; }
    public ScanResult? Scan { get; private set; }
    public bool IsScanning { get; private set; }
    public Dictionary<Guid, ProbeResult> Probes { get; } = new();
    public HashSet<Guid> ProbingDevices { get; } = new();
    public Func<AskPassRequest, Task<string?>>? PromptHandler { get; set; }

    public event Action? DevicesChanged;
    public event Action? ScanChanged;
    public event Action? SettingsChanged;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        Current = new AppServices(dispatcher);
    }

    private AppServices(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        #if DEBUG
        // The self-test works on throwaway data so it can never touch the real configuration.
        Store = new AppStore(SelfTest.IsEnabled ? SelfTest.DataDirectory : null);
#else
        Store = new AppStore();
#endif
        Store.Load();
        HelperPath = Environment.ProcessPath ?? "EasyTerminal.exe";
        Ssh = new SshRunner(Store, request => PromptHandler?.Invoke(request) ?? Task.FromResult<string?>(null), HelperPath);
    }

    public void NotifyDevicesChanged()
    {
        Store.Save();
        DevicesChanged?.Invoke();
    }

    public void NotifySettingsChanged()
    {
        Store.Save();
        SettingsChanged?.Invoke();
    }

    public TerminalAppearance Appearance
    {
        get
        {
            var s = Store.Data.Settings;
            return new TerminalAppearance(s.FontFamily, (float)s.FontSize, (float)s.LineHeight, ColorScheme.Find(s.ColorScheme), s.CursorBlink, s.BoldIsBright);
        }
    }

    public async Task ScanLocalAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanChanged?.Invoke();
        try
        {
            Scan = await Task.Run(() => LocalScanner.ScanAsync());
        }
        finally
        {
            IsScanning = false;
            ScanChanged?.Invoke();
        }
    }

    public IEnumerable<LocalTarget> LocalTargets(LocalKind kind) =>
        (Scan?.Targets ?? LocalScanner.FindShells()).Where(t => t.Kind == kind);

    public LocalTarget? FindLocal(string key) =>
        (Scan?.Targets ?? LocalScanner.FindShells()).FirstOrDefault(t => t.StableKey == key);

    public LocalTarget DefaultShell =>
        FindLocal(Store.Data.Settings.DefaultShellKey) ?? LocalTargets(LocalKind.Shell).First();

    /// <summary>Probes a device for multiplexer sessions; the result drives the sidebar.</summary>
    public async Task<ProbeResult> ProbeAsync(Device device, Connection? connection = null)
    {
        ProbingDevices.Add(device.Id);
        DevicesChanged?.Invoke();
        try
        {
            bool lookupRegion = Store.Data.Settings.RegionLookup;
            var result = await Task.Run(async () =>
            {
                var probe = await RemoteProbe.RunAsync(Ssh, device, connection, lookupRegion);
                // A host without outbound internet can't ask for itself, so ask about its public address from here.
                if (lookupRegion && probe.Facts is { CountryCode.Length: 0 } facts) await GeoLookup.TryFillAsync(facts, device.Host);
                return probe;
            });
            Probes[device.Id] = result;
            if (result.Facts != null) device.Facts = result.Facts;
            // Only a scan can tell what is installed, so that is when these sessions appear.
            if (result.HasTmux || result.HasHerdr) device.Offer(ConnectionProtocol.Tmux, "tmux");
            if (result.HasDocker) device.Offer(ConnectionProtocol.Docker, "Docker");
            if (result.HasProxmox) device.Offer(ConnectionProtocol.Proxmox, "Proxmox");
            Store.Save();
            return result;
        }
        finally
        {
            ProbingDevices.Remove(device.Id);
            DevicesChanged?.Invoke();
        }
    }

    /// <summary>Runs a management command on a device, reports its error text, then rescans.</summary>
    public async Task RunRemoteActionAsync(Device device, string command, TimeSpan? timeout = null)
    {
        var result = await Task.Run(() => Ssh.RunRemoteAsync(device, null, command, timeout ?? TimeSpan.FromMinutes(2)));
        if (result.ExitCode != 0)
        {
            string message = result.Error.Trim();
            throw new IOException(message.Length > 0 ? message : result.Output.Trim());
        }
        await ProbeAsync(device);
    }

    public Task RunContainerActionAsync(Device device, LocalTarget container, string action) =>
        RunRemoteActionAsync(device, RemoteProbe.DockerActionCommand(action, container.Id, Probes.TryGetValue(device.Id, out var probe) && probe.DockerNeedsSudo));

    public Task StopRemoteSessionAsync(Device device, RemoteSession session) =>
        RunRemoteActionAsync(device, RemoteProbe.StopCommand(session.Kind, session.Name), TimeSpan.FromSeconds(30));

    /// <summary>Asks a Proxmox host for a VM's address through the QEMU guest agent.</summary>
    public async Task<string?> GetVmAddressAsync(Device device, PveGuest guest)
    {
        bool sudo = Probes.TryGetValue(device.Id, out var probe) && probe.ProxmoxNeedsSudo;
        var result = await Task.Run(() => Ssh.RunRemoteAsync(device, null, ProxmoxCommands.AgentInterfaces(guest.Id, sudo), TimeSpan.FromSeconds(20)));
        return result.ExitCode == 0 ? ProxmoxCommands.ParseAgentAddress(result.Output) : null;
    }

    // ---- Port forwarding ---------------------------------------------------------------------

    public sealed class ForwardState
    {
        public SshTunnel? Tunnel { get; set; }
        public bool IsStarting { get; set; }
        public string? Error { get; set; }
    }

    public Dictionary<Guid, ForwardState> Forwards { get; } = new();
    public event Action? ForwardsChanged;

    public bool IsForwardRunning(PortForward rule) => Forwards.TryGetValue(rule.Id, out var state) && (state.IsStarting || state.Tunnel != null);

    public async Task StartForwardAsync(PortForward rule)
    {
        if (IsForwardRunning(rule)) return;
        var state = new ForwardState { IsStarting = true };
        Forwards[rule.Id] = state;
        ForwardsChanged?.Invoke();
        try
        {
            var device = Store.FindDevice(rule.DeviceId) ?? throw new InvalidOperationException("这条规则的设备已被删除。");
            var tunnel = await Task.Run(() => SshTunnel.OpenAsync(Ssh, device, null, PortForwardArguments.For(rule)));
            tunnel.Exited += message => Dispatcher.TryEnqueue(() =>
            {
                state.Tunnel = null;
                state.Error = message.Length > 0 ? message : "SSH 连接已结束";
                tunnel.Dispose();
                ForwardsChanged?.Invoke();
            });
            if (!Forwards.TryGetValue(rule.Id, out var current) || current != state)
            {
                tunnel.Dispose();
                return;
            }
            state.Tunnel = tunnel;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state.Error = ex.Message;
        }
        state.IsStarting = false;
        ForwardsChanged?.Invoke();
    }

    public void StopForward(PortForward rule)
    {
        if (Forwards.Remove(rule.Id, out var state)) state.Tunnel?.Dispose();
        ForwardsChanged?.Invoke();
    }

    public void StartAutoForwards()
    {
        foreach (var rule in Store.Data.PortForwards.Where(r => r.AutoStart)) _ = StartForwardAsync(rule);
    }

    public void StopBackgroundWork()
    {
        foreach (var state in Forwards.Values) state.Tunnel?.Dispose();
        Forwards.Clear();
    }

    // ---- Remote desktop ----------------------------------------------------------------------

    public sealed record RdpRoute(string Host, int Port, string Description, SshTunnel? Tunnel);

    /// <summary>Picks where the RDP client should dial; behind jump hosts that is a local end of an SSH tunnel.</summary>
    public async Task<RdpRoute> OpenRdpRouteAsync(Device device, Connection connection)
    {
        var network = connection.OverrideNetwork ? connection.Network : device.Network;
        var endpoints = HostEndpoint.AllFor(device);
        int PortOf(HostEndpoint endpoint) => connection.Port ?? endpoint.Port ?? 3389;

        if (network.JumpDeviceIds.Count > 0)
        {
            var hop = Store.FindDevice(network.JumpDeviceIds[^1]) ?? throw new InvalidOperationException("跳板设备已不存在，请检查网络配置。");
            var earlier = network.JumpDeviceIds.Take(network.JumpDeviceIds.Count - 1).ToList();
            // The last hop opens the connection to the desktop, so the earlier hops and any proxy lead to it.
            var hopNetwork = earlier.Count == 0 && network.ProxyKind == ProxyKind.None
                ? hop.Network
                : new NetworkProfile { ProxyKind = network.ProxyKind, ProxyHost = network.ProxyHost, ProxyPort = network.ProxyPort, ProxyUser = network.ProxyUser, ProxySecretId = network.ProxySecretId, JumpDeviceIds = earlier };
            int localPort = SshTunnel.FindFreePort();
            string target = endpoints[0].Host.Contains(':') ? $"[{endpoints[0].Host}]" : endpoints[0].Host;
            var arguments = new[] { "-L", $"127.0.0.1:{localPort}:{target}:{PortOf(endpoints[0])}" };
            var tunnel = await Task.Run(() => SshTunnel.OpenAsync(Ssh, hop, new Connection { OverrideNetwork = true, Network = hopNetwork }, arguments));
            try
            {
                await tunnel.WaitForLocalPortAsync(localPort, TimeSpan.FromMinutes(2));
            }
            catch
            {
                tunnel.Dispose();
                throw;
            }
            return new RdpRoute("127.0.0.1", localPort, $"经 {hop.Name} 隧道连接", tunnel);
        }

        if (network.ProxyKind != ProxyKind.None)
            throw new InvalidOperationException("RDP 不能直接走代理。请改用跳板设备，流量会通过 SSH 隧道转发。");

        foreach (var endpoint in endpoints)
        {
            if (endpoints.Count == 1 || await IsReachableAsync(endpoint.Host, PortOf(endpoint)))
                return new RdpRoute(endpoint.Host, PortOf(endpoint), $"{endpoint.Host}:{PortOf(endpoint)}", null);
        }
        return new RdpRoute(endpoints[0].Host, PortOf(endpoints[0]), $"{endpoints[0].Host}:{PortOf(endpoints[0])}", null);
    }

    private static async Task<bool> IsReachableAsync(string host, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public ITerminalTransport CreateTelnetTransport(Device device, Connection connection)
    {
        var endpoint = HostEndpoint.Parse(device.Host);
        return new TelnetTransport(endpoint.Host, connection.Port ?? endpoint.Port ?? 23);
    }

    public string NewSessionLogPath(string title)
    {
        string safe = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(SessionLogDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.log");
    }

    public static string SessionLogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".easyterminal", "logs");

    // ---- Transports --------------------------------------------------------------------------

    public ITerminalTransport CreateSshTransport(Device device, Connection? connection, string? remoteCommand) =>
        new DeferredTransport(async (progress, cancellation) =>
        {
            var context = await Ssh.PrepareAsync(device, connection, true, progress, cancellation);
            try
            {
                return new ConPtyTransport(Ssh.BuildTerminalCommand(context, remoteCommand), null, context.Environment, context.Dispose);
            }
            catch
            {
                context.Dispose();
                throw;
            }
        });

    public ITerminalTransport CreateMoshTransport(Device device, Connection? connection) =>
        new MoshTransport(cancellation => BootstrapMoshAsync(device, connection, cancellation));

    private async Task<MoshEndpoint> BootstrapMoshAsync(Device device, Connection? connection, CancellationToken cancellation)
    {
        using var context = await Ssh.PrepareAsync(device, connection, false, null, cancellation);
        using var process = Ssh.StartBackground(context, "-T", context.Alias, "sh -lc " + ShellQuote.Sh(MoshBootstrap.ServerCommand));
        process.StandardInput.Close();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(TimeSpan.FromSeconds(60));
        var output = new System.Text.StringBuilder();
        var errors = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(linked.Token) is { } line)
            {
                output.AppendLine(line);
                if (MoshBootstrap.ParseConnectLine(line) is { } parsed)
                {
                    // The server has daemonized; the SSH session is no longer needed.
                    TryKill(process);
                    return new MoshEndpoint(context.TargetHost, parsed.Port, parsed.Key);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("等待 mosh-server 启动超时。");
        }

        string details = (output + "\n" + await errors).Trim();
        if (details.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new IOException("远程主机没有安装 mosh-server，请先安装 mosh。");
        throw new IOException(details.Length > 0 ? details : "mosh-server 没有返回连接信息。");
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task<SftpClient> OpenSftpAsync(Device device, Connection? connection)
    {
        var context = await Ssh.PrepareAsync(device, connection, false, null, CancellationToken.None);
        var process = Ssh.StartBackground(context, "-T", "-s", context.Alias, "sftp");
        var errors = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (errors) errors.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        var client = new SftpClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, () =>
        {
            TryKill(process);
            process.Dispose();
            context.Dispose();
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.InitializeAsync(timeout.Token);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            string message;
            lock (errors) message = errors.ToString().Trim();
            throw new IOException(message.Length > 0 ? message : ex.Message);
        }
    }
}
