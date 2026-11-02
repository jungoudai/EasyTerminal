using System.Text;
using System.Text.Json;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;

namespace EasyTerminal.Core.Local;

public sealed record ScanStatus(LocalKind Kind, bool Installed, string Message);

public sealed record ScanResult(IReadOnlyList<LocalTarget> Targets, IReadOnlyList<ScanStatus> Statuses);

/// <summary>Finds local environments that can host a terminal: shells, WSL distros, containers and VMs.</summary>
public static class LocalScanner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static async Task<ScanResult> ScanAsync(CancellationToken cancellation = default)
    {
        var wsl = ScanWslAsync(cancellation);
        var docker = ScanDockerAsync(cancellation);
        var multipass = ScanMultipassAsync(cancellation);
        await Task.WhenAll(wsl, docker, multipass);

        var targets = new List<LocalTarget>(FindShells());
        var statuses = new List<ScanStatus>();
        foreach (var (found, status) in new[] { wsl.Result, docker.Result, multipass.Result })
        {
            targets.AddRange(found);
            statuses.Add(status);
        }
        return new ScanResult(targets, statuses);
    }

    public static IReadOnlyList<LocalTarget> FindShells()
    {
        var shells = new List<LocalTarget>();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        string? pwsh = FindOnPath("pwsh.exe");
        if (pwsh != null) shells.Add(Shell("pwsh", "PowerShell 7", pwsh));
        string powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell)) shells.Add(Shell("powershell", "Windows PowerShell", powershell));
        shells.Add(Shell("cmd", "命令提示符", Path.Combine(system, "cmd.exe")));

        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string bash = Path.Combine(Environment.GetFolderPath(root), "Git", "bin", "bash.exe");
            if (File.Exists(bash))
            {
                shells.Add(new LocalTarget(LocalKind.Shell, "gitbash", "Git Bash", bash, true, new[] { bash, "--login", "-i" }));
                break;
            }
        }
        return shells;

        static LocalTarget Shell(string id, string name, string path) => new(LocalKind.Shell, id, name, path, true, new[] { path });
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- WSL --------------------------------------------------------------------------------

    private static async Task<(IReadOnlyList<LocalTarget>, ScanStatus)> ScanWslAsync(CancellationToken cancellation)
    {
        string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl)) return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Wsl, false, "未安装 WSL"));
        try
        {
            // wsl.exe writes UTF-16 unless told otherwise.
            var result = await ProcessRunner.RunAsync(wsl, new[] { "--list", "--verbose" }, Timeout, Encoding.Unicode, cancellation);
            if (result.ExitCode != 0) return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Wsl, false, "未安装 WSL 或没有发行版"));
            var targets = ParseWsl(result.Output, wsl);
            return (targets, new ScanStatus(LocalKind.Wsl, true, $"{targets.Count} 个发行版"));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Wsl, false, ex.Message));
        }
    }

    public static IReadOnlyList<LocalTarget> ParseWsl(string output, string wslPath = "wsl.exe")
    {
        var targets = new List<LocalTarget>();
        bool first = true;
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.Replace("\0", "").Trim();
            if (line.Length == 0) continue;
            // The header is localized, so it is skipped by position rather than by text.
            if (first)
            {
                first = false;
                continue;
            }
            bool isDefault = line.StartsWith('*');
            if (isDefault) line = line[1..].Trim();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            string version = parts[^1];
            string state = parts[^2];
            string name = string.Join(' ', parts[..^2]);
            bool running = state.Equals("Running", StringComparison.OrdinalIgnoreCase) || state.Contains("运行");
            string detail = $"WSL{version} · {state}" + (isDefault ? " · 默认" : "");
            targets.Add(new LocalTarget(LocalKind.Wsl, name, name, detail, running, new[] { wslPath, "-d", name, "--cd", "~" }));
        }
        return targets;
    }

    // ---- Docker -----------------------------------------------------------------------------

    private static async Task<(IReadOnlyList<LocalTarget>, ScanStatus)> ScanDockerAsync(CancellationToken cancellation)
    {
        string? docker = FindOnPath("docker.exe");
        if (docker == null) return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Docker, false, "未安装 Docker"));
        try
        {
            var result = await ProcessRunner.RunAsync(docker, new[] { "ps", "-a", "--format", "{{json .}}" }, Timeout, null, cancellation);
            if (result.ExitCode != 0) return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Docker, true, "Docker 引擎未运行"));
            var targets = ParseDocker(result.Output, docker);
            return (targets, new ScanStatus(LocalKind.Docker, true, $"{targets.Count} 个容器"));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Docker, true, "Docker 没有响应"));
        }
    }

    public static IReadOnlyList<LocalTarget> ParseDocker(string output, string dockerPath = "docker.exe")
    {
        var targets = new List<LocalTarget>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                string id = root.GetProperty("ID").GetString() ?? "";
                string name = root.GetProperty("Names").GetString() ?? id;
                string image = root.TryGetProperty("Image", out var i) ? i.GetString() ?? "" : "";
                string state = root.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "";
                bool running = state.Equals("running", StringComparison.OrdinalIgnoreCase);
                var command = new[] { dockerPath, "exec", "-it", id, "sh", "-c", "command -v bash >/dev/null && exec bash || exec sh" };
                targets.Add(new LocalTarget(LocalKind.Docker, id, name, $"{image} · {state}", running, command));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // A daemon warning line is not JSON; skip it and keep the containers.
            }
        }
        return targets;
    }

    public static string[] DockerStartArguments(LocalTarget target) => new[] { "start", target.Id };

    // ---- Multipass --------------------------------------------------------------------------

    private static async Task<(IReadOnlyList<LocalTarget>, ScanStatus)> ScanMultipassAsync(CancellationToken cancellation)
    {
        string? multipass = FindOnPath("multipass.exe");
        if (multipass == null)
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Multipass", "bin", "multipass.exe");
            if (File.Exists(fallback)) multipass = fallback;
        }
        if (multipass == null) return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Multipass, false, "未安装 Multipass"));
        try
        {
            var result = await ProcessRunner.RunAsync(multipass, new[] { "list", "--format", "json" }, Timeout, null, cancellation);
            if (result.ExitCode != 0) return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Multipass, true, "Multipass 服务未运行"));
            var targets = ParseMultipass(result.Output, multipass);
            return (targets, new ScanStatus(LocalKind.Multipass, true, $"{targets.Count} 个实例"));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            return (Array.Empty<LocalTarget>(), new ScanStatus(LocalKind.Multipass, true, "Multipass 没有响应"));
        }
    }

    public static IReadOnlyList<LocalTarget> ParseMultipass(string output, string multipassPath = "multipass.exe")
    {
        var targets = new List<LocalTarget>();
        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("list", out var list)) return targets;
            foreach (var item in list.EnumerateArray())
            {
                string name = item.GetProperty("name").GetString() ?? "";
                string state = item.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
                string release = item.TryGetProperty("release", out var r) ? r.GetString() ?? "" : "";
                bool running = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
                targets.Add(new LocalTarget(LocalKind.Multipass, name, name, $"{release} · {state}", running, new[] { multipassPath, "shell", name }));
            }
        }
        catch (JsonException)
        {
        }
        return targets;
    }

    /// <summary>Starts a stopped container or VM so it can be attached to.</summary>
    public static async Task StartTargetAsync(LocalTarget target, CancellationToken cancellation = default)
    {
        string tool = target.Command[0];
        string[]? arguments = target.Kind switch
        {
            LocalKind.Docker => new[] { "start", target.Id },
            LocalKind.Multipass => new[] { "start", target.Id },
            _ => null,
        };
        if (arguments == null) return;
        var result = await ProcessRunner.RunAsync(tool, arguments, TimeSpan.FromMinutes(3), null, cancellation);
        if (result.ExitCode != 0) throw new IOException(result.Error.Trim());
    }

    public static async Task StopTargetAsync(LocalTarget target, CancellationToken cancellation = default)
    {
        string tool = target.Command[0];
        string[] arguments = target.Kind switch
        {
            LocalKind.Docker => new[] { "stop", target.Id },
            LocalKind.Multipass => new[] { "stop", target.Id },
            LocalKind.Wsl => new[] { "--terminate", target.Id },
            _ => throw new InvalidOperationException("本地 Shell 没有可停止的环境。"),
        };
        var result = await ProcessRunner.RunAsync(tool, arguments, TimeSpan.FromMinutes(2), target.Kind == LocalKind.Wsl ? Encoding.Unicode : null, cancellation);
        if (result.ExitCode != 0) throw new IOException(result.Error.Trim());
    }
}
