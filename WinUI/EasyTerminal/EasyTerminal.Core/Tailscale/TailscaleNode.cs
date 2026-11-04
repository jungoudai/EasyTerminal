using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EasyTerminal.Core.Tailscale;

/// <summary>How the embedded node is started. Read each time it starts, so settings changes apply on restart.</summary>
public sealed record TailscaleOptions(string HelperPath, string StateDirectory, string Hostname, string ControlUrl, string? AuthKey, bool Ephemeral);

public sealed record TailscalePeer(string HostName, string DnsName, IReadOnlyList<string> Addresses, string Os, bool Online, bool IsSelf)
{
    /// <summary>MagicDNS name without the trailing dot, falling back to the first tailnet address.</summary>
    public string BestAddress => DnsName.Length > 0 ? DnsName.TrimEnd('.') : Addresses.FirstOrDefault() ?? HostName;
}

public sealed record TailscaleStatus(string BackendState, string AuthUrl, TailscalePeer? Self, string Tailnet, IReadOnlyList<TailscalePeer> Peers, IReadOnlyList<string> Health)
{
    public bool IsRunning => BackendState == "Running";
    public bool NeedsLogin => BackendState is "NeedsLogin" or "NeedsMachineAuth";
}

/// <summary>Where the node's SOCKS5 proxy listens. Only valid while the node runs.</summary>
public sealed record TailscaleProxy(string Host, int Port, string User, string Password);

/// <summary>
/// An embedded Tailscale node. The tsnet library runs in a helper process (easyterminal-tailscale.exe,
/// built from EasyTerminal.Tailscale) so this machine joins the tailnet without the Tailscale client,
/// a TUN adapter or administrator rights. Traffic reaches the tailnet through the node's SOCKS5 proxy;
/// login, status and logout go through its LocalAPI. Both listen on one authenticated loopback port.
/// </summary>
public sealed class TailscaleNode : IDisposable
{
    public const string HelperFileName = "easyterminal-tailscale.exe";

    public static TailscaleNode Shared { get; } = new();

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Queue<string> _log = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private Process? _process;
    private TailscaleProxy? _proxy;
    private string _localApiBase = "";

    /// <summary>Supplies settings when the node starts; set once by the app.</summary>
    public Func<TailscaleOptions>? OptionsProvider { get; set; }

    public event Action? StateChanged;

    public bool IsHelperAvailable => OptionsProvider != null && File.Exists(OptionsProvider().HelperPath);
    public bool IsStarted => _proxy != null && _process is { HasExited: false };
    public bool IsStarting { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The most recent lines the helper wrote to stderr, for diagnostics.</summary>
    public string RecentLog
    {
        get
        {
            lock (_log) return string.Join('\n', _log);
        }
    }

    /// <summary>The SOCKS5 endpoint; throws a readable error when the node is not running.</summary>
    public TailscaleProxy Proxy => IsStarted ? _proxy! : throw new InvalidOperationException("内置 Tailscale 未启动。请在 设置 → Tailscale 中启动并登录。");

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        await _startLock.WaitAsync(cancellation);
        try
        {
            if (IsStarted) return;
            StopProcess();
            var options = OptionsProvider?.Invoke() ?? throw new InvalidOperationException("内置 Tailscale 未配置。");
            if (!File.Exists(options.HelperPath))
                throw new FileNotFoundException("没有找到内置 Tailscale 组件 " + HelperFileName + "。构建时需要 Go 工具链。", options.HelperPath);

            IsStarting = true;
            LastError = null;
            StateChanged?.Invoke();
            Directory.CreateDirectory(options.StateDirectory);
            var info = new ProcessStartInfo(options.HelperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.ArgumentList.Add("-dir");
            info.ArgumentList.Add(options.StateDirectory);
            info.ArgumentList.Add("-hostname");
            info.ArgumentList.Add(options.Hostname.Length > 0 ? options.Hostname : "easyterminal");
            if (options.ControlUrl.Length > 0)
            {
                info.ArgumentList.Add("-control-url");
                info.ArgumentList.Add(options.ControlUrl);
            }
            if (options.Ephemeral) info.ArgumentList.Add("-ephemeral");
            // The key travels in the environment so it never appears in a process list.
            if (!string.IsNullOrEmpty(options.AuthKey)) info.Environment["EASYTERMINAL_TS_AUTHKEY"] = options.AuthKey;

            var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动内置 Tailscale。");
            _process = process;
            process.EnableRaisingEvents = true;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_log)
                {
                    _log.Enqueue(e.Data);
                    while (_log.Count > 80) _log.Dequeue();
                }
            };
            process.BeginErrorReadLine();
            process.Exited += (_, _) =>
            {
                if (_process != process) return;
                _proxy = null;
                LastError ??= "内置 Tailscale 已退出。";
                StateChanged?.Invoke();
            };

            // The first stdout line says where the node listens. Contacting the control server can take a while.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                StopProcess();
                throw new TimeoutException("内置 Tailscale 启动超时。" + LastLogLine());
            }
            if (line == null)
            {
                StopProcess();
                throw new InvalidOperationException("内置 Tailscale 启动失败。" + LastLogLine());
            }

            using var ready = JsonDocument.Parse(line);
            var root = ready.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                StopProcess();
                throw new InvalidOperationException("内置 Tailscale 启动失败：" + error.GetString());
            }
            string addr = root.GetProperty("addr").GetString()!;
            int colon = addr.LastIndexOf(':');
            _proxy = new TailscaleProxy(addr[..colon], int.Parse(addr[(colon + 1)..]), root.GetProperty("proxyUser").GetString()!, root.GetProperty("proxyPassword").GetString()!);
            _localApiBase = "http://" + addr + "/localapi/v0/";
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + root.GetProperty("localApiPassword").GetString())));
            _http.DefaultRequestHeaders.Remove("Sec-Tailscale");
            _http.DefaultRequestHeaders.Add("Sec-Tailscale", "localapi");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            throw;
        }
        finally
        {
            IsStarting = false;
            _startLock.Release();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Starts the node if needed and waits until it is signed in, for connections that go through it.</summary>
    public async Task EnsureRunningAsync(Action<string>? progress, CancellationToken cancellation)
    {
        if (!IsStarted)
        {
            progress?.Invoke("正在启动内置 Tailscale");
            await StartAsync(cancellation);
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var status = await GetStatusAsync(cancellation);
            if (status.IsRunning) return;
            if (status.NeedsLogin) throw new InvalidOperationException("内置 Tailscale 尚未登录。请在 设置 → Tailscale 中登录后再连接。");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"内置 Tailscale 未就绪（{status.BackendState}）。");
            progress?.Invoke($"等待 Tailscale 就绪（{status.BackendState}）");
            await Task.Delay(1000, cancellation);
        }
    }

    public async Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellation = default)
    {
        _ = Proxy;
        using var response = await _http.GetAsync(_localApiBase + "status", cancellation);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), default, cancellation);
        return ParseStatus(json.RootElement);
    }

    /// <summary>Asks the control server for a fresh sign-in page; its address shows up in the status shortly after.</summary>
    public async Task<string> LoginInteractiveAsync(CancellationToken cancellation = default)
    {
        var status = await GetStatusAsync(cancellation);
        if (status.AuthUrl.Length > 0) return status.AuthUrl;
        using (var response = await _http.PostAsync(_localApiBase + "login-interactive", null, cancellation))
            response.EnsureSuccessStatusCode();
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500, cancellation);
            status = await GetStatusAsync(cancellation);
            if (status.AuthUrl.Length > 0) return status.AuthUrl;
            if (status.IsRunning) return "";
        }
        throw new TimeoutException("没有拿到 Tailscale 登录地址。" + LastLogLine());
    }

    public async Task LogoutAsync(CancellationToken cancellation = default)
    {
        using var response = await _http.PostAsync(_localApiBase + "logout", null, cancellation);
        response.EnsureSuccessStatusCode();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        StopProcess();
        LastError = null;
        StateChanged?.Invoke();
    }

    public void Dispose() => StopProcess();

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _proxy = null;
        if (process == null) return;
        try
        {
            // Closing stdin asks the node to shut down cleanly, which matters for ephemeral nodes.
            process.StandardInput.Close();
            if (!process.WaitForExit(3000)) process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }
        process.Dispose();
    }

    private string LastLogLine()
    {
        lock (_log) return _log.Count > 0 ? "\n" + _log.Last() : "";
    }

    public static TailscaleStatus ParseStatus(JsonElement root)
    {
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static TailscalePeer Peer(JsonElement e, bool self)
        {
            var addresses = e.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? ips.EnumerateArray().Select(i => i.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
            bool online = self || (e.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True);
            return new TailscalePeer(Str(e, "HostName"), Str(e, "DNSName"), addresses, Str(e, "OS"), online, self);
        }

        var peers = new List<TailscalePeer>();
        if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            foreach (var entry in peerMap.EnumerateObject()) peers.Add(Peer(entry.Value, false));
        peers.Sort((a, b) => a.Online != b.Online ? (a.Online ? -1 : 1) : string.Compare(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase));

        TailscalePeer? selfPeer = root.TryGetProperty("Self", out var selfElement) && selfElement.ValueKind == JsonValueKind.Object ? Peer(selfElement, true) : null;
        string tailnet = root.TryGetProperty("CurrentTailnet", out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "Name") : "";
        var health = root.TryGetProperty("Health", out var h) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();
        return new TailscaleStatus(Str(root, "BackendState"), Str(root, "AuthURL"), selfPeer, tailnet, peers, health);
    }
}
