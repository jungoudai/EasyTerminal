using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Transport;

namespace EasyTerminal.Core.Ssh;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>A generated config file plus the environment that goes with it. Dispose removes the file.</summary>
public sealed class SshContext : IDisposable
{
    private readonly Action _cleanup;

    internal SshContext(string configPath, string alias, string targetHost, IReadOnlyDictionary<string, string> environment, Action cleanup)
    {
        TargetHost = targetHost;
        ConfigPath = configPath;
        Alias = alias;
        Environment = environment;
        _cleanup = cleanup;
    }

    public string ConfigPath { get; }
    public string Alias { get; }
    /// <summary>The address of the device that this config dials.</summary>
    public string TargetHost { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }

    public void Dispose() => _cleanup();
}

/// <summary>Launches the system OpenSSH tools with generated configs and in-app prompts.</summary>
public sealed class SshRunner
{
    private readonly AppStore _store;
    private readonly AskPassServer _askPass;
    private readonly string _helperPath;
    private readonly string _runDirectory;
    private readonly AddressResolver _resolver;
    private readonly ConcurrentDictionary<string, SavedPasswords> _passwords = new();
    private readonly ConcurrentDictionary<string, string> _keyPassphrases = new();

    public SshRunner(AppStore store, Func<AskPassRequest, Task<string?>> prompt, string helperPath)
    {
        _store = store;
        _helperPath = helperPath;
        _resolver = new AddressResolver(store.Secrets.Get);
        // Not under AppData: MSIX virtualizes that for this app, and ssh.exe would look at the real path.
        _runDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".easyterminal", "run");
        Directory.CreateDirectory(_runDirectory);
        foreach (var stale in Directory.EnumerateFiles(_runDirectory, "*.conf"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
            }
        }
        _askPass = new AskPassServer(request => HandlePrompt(request, prompt));
    }

    public static string SshPath => FindTool("ssh.exe");
    public static string SshKeygenPath => FindTool("ssh-keygen.exe");

    public static bool IsOpenSshAvailable => File.Exists(SshPath);

    private static string FindTool(string name)
    {
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", name);
        if (File.Exists(system)) return system;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), name);
            if (File.Exists(candidate)) return candidate;
        }
        return system;
    }

    private async Task<string?> HandlePrompt(AskPassRequest request, Func<AskPassRequest, Task<string?>> prompt)
    {
        if (request.Kind == AskPassKind.Secret && _passwords.TryGetValue(request.Token, out var saved))
        {
            string? password = saved.TryTake(request.Prompt);
            if (password != null) return password;
        }
        if (request.Kind == AskPassKind.Secret && _keyPassphrases.TryGetValue(request.Token, out var passphrase) && request.Prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
            return passphrase;
        return await prompt(request);
    }

    /// <summary>
    /// Picks an address for each hop that this machine dials itself, then prepares the connection.
    /// Behind a jump host only that host can see the target's network, so the target's first address is used.
    /// </summary>
    public async Task<SshContext> PrepareAsync(Device device, Connection? connection, bool interactiveTerminal, Action<string>? progress, CancellationToken cancellation)
    {
        var network = connection is { OverrideNetwork: true } ? connection.Network : device.Network;
        var chosen = new Dictionary<Guid, HostEndpoint>();
        var proxy = network.ProxyKind != ProxyKind.None ? network : null;
        if (ProxyRoute.UsesTailscale(network, _store.FindDevice))
            await Tailscale.TailscaleNode.Shared.EnsureRunningAsync(progress, cancellation);
        if (network.JumpDeviceIds.Count > 0)
        {
            if (device.AlternateAddresses.Count > 0) progress?.Invoke("经跳板连接时只使用第一个地址");
            if (_store.FindDevice(network.JumpDeviceIds[0]) is { } firstHop)
            {
                var hopProxy = proxy ?? (firstHop.Network.ProxyKind != ProxyKind.None ? firstHop.Network : null);
                chosen[firstHop.Id] = await _resolver.ChooseAsync(firstHop, null, hopProxy, progress, cancellation);
            }
        }
        else
        {
            chosen[device.Id] = await _resolver.ChooseAsync(device, connection?.Port, proxy, progress, cancellation);
        }
        return Prepare(device, connection, interactiveTerminal, chosen);
    }

    /// <summary>Prepares a connection. Prompts go through the app when <paramref name="interactiveTerminal"/> is false.</summary>
    public SshContext Prepare(Device device, Connection? connection, bool interactiveTerminal, IReadOnlyDictionary<Guid, HostEndpoint>? chosenAddresses = null)
    {
        string? userConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(userConfig)) userConfig = null;

        var builder = new SshConfigBuilder(_store.FindDevice, _store.Secrets.Get, _helperPath, userConfig);
        var plan = builder.Build(device, connection, chosenAddresses);
        string targetHost = chosenAddresses != null && chosenAddresses.TryGetValue(device.Id, out var target) ? target.Host : HostEndpoint.Parse(device.Host).Host;

        string configPath = Path.Combine(_runDirectory, Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(configPath, plan.ConfigText, new UTF8Encoding(false));

        var environment = new Dictionary<string, string>(plan.Environment);
        var passwords = CollectPasswords(device, connection);
        string token = Guid.NewGuid().ToString("N");

        // Interactive sessions prompt in the terminal itself unless there is a saved password to supply.
        if (!interactiveTerminal || passwords.Count > 0)
        {
            environment["SSH_ASKPASS"] = _helperPath;
            environment["SSH_ASKPASS_REQUIRE"] = "force";
            environment[SshHelperEntry.AskPassPipeVariable] = _askPass.PipeName;
            environment[SshHelperEntry.AskPassTokenVariable] = token;
            if (passwords.Count > 0) _passwords[token] = new SavedPasswords(passwords);
        }

        return new SshContext(configPath, plan.Alias, targetHost, environment, () =>
        {
            _passwords.TryRemove(token, out _);
            try
            {
                File.Delete(configPath);
            }
            catch (IOException)
            {
            }
        });
    }

    private Dictionary<string, string> CollectPasswords(Device device, Connection? connection)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(Device d)
        {
            if (d.Auth != AuthMethod.Password || _store.Secrets.Get(d.PasswordSecretId) is not { } secret) return;
            foreach (var endpoint in HostEndpoint.AllFor(d)) result[endpoint.Host] = secret;
        }

        Add(device);
        var network = connection is { OverrideNetwork: true } ? connection.Network : device.Network;
        foreach (var hopId in network.JumpDeviceIds)
            if (_store.FindDevice(hopId) is { } hop) Add(hop);
        return result;
    }

    /// <summary>Builds the command line for an interactive session under a pseudo console.</summary>
    public string BuildTerminalCommand(SshContext context, string? remoteCommand)
    {
        var args = new List<string> { SshPath, "-F", context.ConfigPath, "-t", context.Alias };
        if (!string.IsNullOrWhiteSpace(remoteCommand)) args.Add(remoteCommand);
        return CommandLine.Join(args);
    }

    /// <summary>Generates a key with prompts routed to the app, answering passphrase questions itself.</summary>
    public async Task<SshKeyInfo> GenerateKeyAsync(KeyGenerationOptions options, string token, CancellationToken cancellation)
    {
        if (options.Passphrase.Length > 0) _keyPassphrases[token] = options.Passphrase;
        try
        {
            return await KeyManager.GenerateAsync(options, _helperPath, _askPass.PipeName, token, cancellation);
        }
        finally
        {
            _keyPassphrases.TryRemove(token, out _);
        }
    }

    public Process StartBackground(SshContext context, params string[] extraArguments)
    {
        var info = new ProcessStartInfo(SshPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-F");
        info.ArgumentList.Add(context.ConfigPath);
        foreach (var argument in extraArguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in context.Environment) info.Environment[key] = value;
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动 ssh.exe");
    }

    public async Task<ProcessResult> RunRemoteAsync(Device device, Connection? connection, string script, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var context = await PrepareAsync(device, connection, false, null, cancellation);
        using var process = StartBackground(context, "-T", context.Alias, "sh -lc " + ShellQuote.Sh(script));
        process.StandardInput.Close();
        return await ProcessRunner.CollectAsync(process, timeout, cancellation);
    }

    /// <summary>Hands out each saved password once per host, so a wrong one falls through to the user.</summary>
    private sealed class SavedPasswords(Dictionary<string, string> passwords)
    {
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        public string? TryTake(string prompt)
        {
            if (!prompt.Contains("password", StringComparison.OrdinalIgnoreCase)) return null;
            lock (_used)
            {
                foreach (var (host, password) in passwords)
                    if (prompt.Contains(host, StringComparison.OrdinalIgnoreCase) && _used.Add(host)) return password;
                // Keyboard-interactive prompts often omit the host; only one distinct password is unambiguous.
                if (passwords.Values.Distinct().Count() == 1 && _used.Add("*")) return passwords.Values.First();
                return null;
            }
        }
    }
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, TimeSpan timeout, Encoding? encoding = null, CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 " + file);
        process.StandardInput.Close();
        return await CollectAsync(process, timeout, cancellation);
    }

    public static async Task<ProcessResult> CollectAsync(Process process, TimeSpan timeout, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(linked.Token);
        var error = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            return new ProcessResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException($"命令在 {timeout.TotalSeconds:0} 秒内没有完成");
        }
    }
}
