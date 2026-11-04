using System.Globalization;
using System.Text.Json;
using EasyTerminal.Core.Local;
using EasyTerminal.Core.Models;

namespace EasyTerminal.Core.Ssh;

public enum RemoteSessionKind { Tmux, Herdr }
public enum PveGuestKind { Lxc, Qemu }

public sealed record RemoteSession(RemoteSessionKind Kind, string Name, int Windows, bool IsActive, DateTimeOffset? Created, string Detail);

public sealed record TmuxWindow(string Session, int Index, string Name, int Panes, bool IsActive, string Command);

public sealed record PveGuest(PveGuestKind Kind, int Id, string Name, string Status, string Detail)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRunning => Status.Equals("running", StringComparison.OrdinalIgnoreCase);
}

public sealed record ProbeResult(bool IsTermux, bool HasTmux, bool HasHerdr, bool HasMosh, IReadOnlyList<RemoteSession> Sessions, bool HasDocker, string? DockerError, IReadOnlyList<LocalTarget> Containers)
{
    public IReadOnlyList<TmuxWindow> Windows { get; init; } = Array.Empty<TmuxWindow>();
    public bool DockerNeedsSudo { get; init; }
    public DeviceFacts? Facts { get; init; }
    public bool HasProxmox { get; init; }
    public string ProxmoxNode { get; init; } = "";
    public bool ProxmoxNeedsSudo { get; init; }
    public string? ProxmoxError { get; init; }
    public IReadOnlyList<PveGuest> Guests { get; init; } = Array.Empty<PveGuest>();
}

/// <summary>Finds sessions, containers, guests and system facts on a remote host with one read-only script over SSH.</summary>
public static class RemoteProbe
{
    private const string GeoUrls = "https://ipinfo.io/json https://ipapi.co/json/";

    // Output is split into marked sections so one missing tool cannot garble another's parse.
    public static readonly string Script =
        "PATH=\"$PATH:/usr/sbin:/sbin\";" +
        "echo @@ENV;" +
        "if [ -n \"$TERMUX_VERSION\" ] || [ -d /data/data/com.termux ]; then echo termux; fi;" +
        "command -v mosh-server >/dev/null 2>&1 && echo mosh;" +
        "echo @@SYS;" +
        "echo \"host=$(hostname 2>/dev/null)\";" +
        "echo \"kernel=$(uname -sr 2>/dev/null)\";" +
        "echo \"arch=$(uname -m 2>/dev/null)\";" +
        "if [ -r /etc/os-release ]; then echo \"os=$(. /etc/os-release; echo \"$PRETTY_NAME\")\";" +
        "elif command -v sw_vers >/dev/null 2>&1; then echo \"os=macOS $(sw_vers -productVersion)\"; fi;" +
        "echo \"cpus=$(nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null)\";" +
        "if [ -r /proc/meminfo ]; then awk '/^MemTotal:/{print \"memkb=\"$2}/^MemAvailable:/{print \"memfreekb=\"$2}' /proc/meminfo;" +
        "else echo \"memkb=$(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1024 ))\"; fi;" +
        "if [ -r /proc/loadavg ]; then echo \"load=$(cut -d' ' -f1-3 /proc/loadavg)\";" +
        "else echo \"load=$(uptime | sed 's/.*load averages*: //;s/,//g')\"; fi;" +
        "[ -r /proc/uptime ] && echo \"uptime=$(cut -d. -f1 /proc/uptime)\";" +
        "command -v systemd-detect-virt >/dev/null 2>&1 && echo \"virt=$(systemd-detect-virt 2>/dev/null)\";" +
        "command -v pveversion >/dev/null 2>&1 && echo \"pve=$(pveversion 2>/dev/null)\";" +
        "echo \"uid=$(id -u 2>/dev/null)\";" +
        "if command -v tmux >/dev/null 2>&1; then echo @@ENV; echo tmux; echo @@TMUX;" +
        "tmux list-sessions -F '#{session_windows}|#{session_attached}|#{session_created}|#{session_name}' 2>/dev/null;" +
        "echo @@TMUXWIN; T=$(printf '\\t');" +
        "tmux list-windows -a -F \"#{window_index}${T}#{window_panes}${T}#{window_active}${T}#{pane_current_command}${T}#{session_name}${T}#{window_name}\" 2>/dev/null; fi;" +
        "if command -v herdr >/dev/null 2>&1; then echo @@HERDR;" +
        "herdr session list --json 2>/dev/null; fi;" +
        "if command -v docker >/dev/null 2>&1; then D=$(docker ps -a --format '{{json .}}' 2>&1);" +
        "case \"$D\" in *\"permission denied\"*) if S=$(sudo -n docker ps -a --format '{{json .}}' 2>/dev/null); then echo @@DOCKERSUDO; echo \"$S\"; else echo @@DOCKER; echo \"$D\"; fi;;" +
        "*) echo @@DOCKER; echo \"$D\";; esac; fi;" +
        "if command -v pct >/dev/null 2>&1 || command -v qm >/dev/null 2>&1; then P=''; [ \"$(id -u)\" = 0 ] || { sudo -n true 2>/dev/null && P='sudo -n'; };" +
        "echo @@PVE; echo \"node=$(hostname)\"; echo \"sudo=$P\"; echo \"uid=$(id -u)\";" +
        "echo @@PCT; $P pct list 2>&1; echo @@QM; $P qm list 2>&1; fi;" +
        Monitoring.ToolCatalog.DetectScript +
        "echo @@END";

    /// <summary>The scan script, optionally asking a public service where the machine's own address is.</summary>
    public static string BuildScript(bool lookupRegion) => !lookupRegion ? Script : Script.Replace(
        "echo @@END",
        "echo @@GEO; for u in " + GeoUrls + "; do if command -v curl >/dev/null 2>&1; then curl -fsS -m 4 \"$u\" 2>/dev/null && break; " +
        "elif command -v wget >/dev/null 2>&1; then wget -qO- -T 4 \"$u\" 2>/dev/null && break; fi; done; echo; echo @@END");

    public static async Task<ProbeResult> RunAsync(SshRunner runner, Device device, Connection? connection, bool lookupRegion = false, CancellationToken cancellation = default)
    {
        var result = await runner.RunRemoteAsync(device, connection, BuildScript(lookupRegion), TimeSpan.FromSeconds(40), cancellation);
        if (!result.Output.Contains("@@END", StringComparison.Ordinal))
        {
            string error = result.Error.Trim();
            throw new IOException(error.Length > 0 ? error : $"ssh 退出代码 {result.ExitCode}");
        }
        return Parse(result.Output);
    }

    public static ProbeResult Parse(string output)
    {
        bool termux = false, tmux = false, mosh = false, herdr = false, docker = false, dockerSudo = false, pve = false, pveSudo = false;
        string pveNode = "", pveUid = "";
        var sessions = new List<RemoteSession>();
        var windows = new List<TmuxWindow>();
        var guests = new List<PveGuest>();
        var sys = new Dictionary<string, string>();
        string section = "";
        string? pveError = null;
        var tools = new List<string>();
        string packageManager = "";
        var herdrJson = new System.Text.StringBuilder();
        var dockerOutput = new System.Text.StringBuilder();
        var geoJson = new System.Text.StringBuilder();

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                section = line;
                if (line == "@@HERDR") herdr = true;
                if (line == "@@DOCKER") docker = true;
                if (line == "@@DOCKERSUDO") docker = dockerSudo = true;
                if (line == "@@PVE") pve = true;
                continue;
            }
            switch (section)
            {
                case "@@ENV":
                    if (line == "termux") termux = true;
                    else if (line == "tmux") tmux = true;
                    else if (line == "mosh") mosh = true;
                    break;
                case "@@SYS":
                    int equals = line.IndexOf('=');
                    if (equals > 0) sys[line[..equals]] = line[(equals + 1)..];
                    break;
                case "@@TMUX":
                    if (ParseTmuxLine(line) is { } session) sessions.Add(session);
                    break;
                case "@@TMUXWIN":
                    if (ParseTmuxWindow(line) is { } window) windows.Add(window);
                    break;
                case "@@HERDR":
                    herdrJson.Append(line).Append('\n');
                    break;
                case "@@DOCKER" or "@@DOCKERSUDO":
                    dockerOutput.Append(line).Append('\n');
                    break;
                case "@@GEO":
                    geoJson.Append(line).Append('\n');
                    break;
                case "@@PVE":
                    if (line.StartsWith("node=")) pveNode = line[5..];
                    else if (line.StartsWith("sudo=")) pveSudo = line.Length > 5;
                    else if (line.StartsWith("uid=")) pveUid = line[4..];
                    break;
                case "@@TOOLS":
                    if (line.StartsWith("pm=", StringComparison.Ordinal)) packageManager = line[3..];
                    else if (Monitoring.ToolCatalog.Find(line) != null && !tools.Contains(line)) tools.Add(line);
                    break;
                case "@@PCT" or "@@QM":
                    if (ParsePveLine(line, section == "@@PCT" ? PveGuestKind.Lxc : PveGuestKind.Qemu) is { } guest) guests.Add(guest);
                    else if (IsPveDenied(line)) pveError = "需要 root 权限才能管理虚拟机和容器，请用 root 登录或配置免密 sudo";
                    break;
            }
        }

        sessions.AddRange(ParseHerdr(herdrJson.ToString()));
        string dockerText = dockerOutput.ToString();
        var containers = LocalScanner.ParseDocker(dockerText, "docker");
        string? dockerError = null;
        if (containers.Count == 0 && dockerText.Contains("permission denied", StringComparison.OrdinalIgnoreCase)) dockerError = "当前用户没有 Docker 权限";
        else if (containers.Count == 0 && dockerText.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase)) dockerError = "Docker 引擎未运行";
        if (pve && pveError == null && pveUid != "0" && !pveSudo && guests.Count == 0)
            pveError = "需要 root 权限才能管理虚拟机和容器，请用 root 登录或配置免密 sudo";

        var facts = BuildFacts(sys, geoJson.ToString());
        if (facts != null)
        {
            facts.HasTmux = tmux || herdr;
            facts.HasDocker = docker;
            facts.HasProxmox = pve;
            facts.Tools = tools;
            facts.PackageManager = packageManager;
            facts.IsRoot = sys.TryGetValue("uid", out var uid) && uid.Trim() == "0";
            facts.ProxmoxNode = pveNode;
            facts.ProxmoxNeedsSudo = pveSudo;
            facts.Guests = guests.ToList();
            // Termux has no os-release, and its kernel alone would read as plain Linux.
            if (termux && facts.Os.Length == 0) facts.Os = "Android (Termux)";
        }
        return new ProbeResult(termux, tmux, herdr, mosh, sessions, docker, dockerError, containers)
        {
            Windows = windows,
            DockerNeedsSudo = dockerSudo,
            Facts = facts,
            HasProxmox = pve,
            ProxmoxNode = pveNode,
            ProxmoxNeedsSudo = pveSudo,
            ProxmoxError = pveError,
            Guests = guests,
        };
    }

    private static bool IsPveDenied(string line) =>
        line.Contains("only root", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("not allowed", StringComparison.OrdinalIgnoreCase);

    private static DeviceFacts? BuildFacts(Dictionary<string, string> sys, string geoJson)
    {
        if (sys.Count == 0) return null;
        string Get(string key) => sys.TryGetValue(key, out var value) ? value.Trim() : "";
        var facts = new DeviceFacts
        {
            Hostname = Get("host"),
            Os = Get("os"),
            Kernel = Get("kernel"),
            Architecture = Get("arch"),
            Virtualization = Get("virt") is "none" ? "" : Get("virt"),
            ProxmoxVersion = Get("pve"),
            ScannedAt = DateTimeOffset.Now,
        };
        if (int.TryParse(Get("cpus"), out int cpus)) facts.CpuCount = cpus;
        if (long.TryParse(Get("memkb"), out long memory)) facts.MemoryTotalKb = memory;
        if (long.TryParse(Get("memfreekb"), out long free)) facts.MemoryAvailableKb = free;
        if (long.TryParse(Get("uptime"), out long uptime)) facts.UptimeSeconds = uptime;
        var load = Get("load").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (load.Length >= 3)
        {
            double.TryParse(load[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double one);
            double.TryParse(load[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double five);
            double.TryParse(load[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double fifteen);
            facts.Load1 = one;
            facts.Load5 = five;
            facts.Load15 = fifteen;
        }
        ApplyGeo(facts, geoJson);
        return facts;
    }

    /// <summary>Reads the answer of ipinfo.io or ipapi.co, which share these field names.</summary>
    public static void ApplyGeo(DeviceFacts facts, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            facts.PublicIp = Text("ip");
            facts.CountryCode = Text("country");
            facts.Region = Text("region");
            facts.City = Text("city");
            facts.Network = Text("org");
        }
        catch (JsonException)
        {
            // A captive portal or an error page is not an answer; the location just stays unknown.
        }
    }

    /// <summary>Parses "windows|attached|created|name"; the name is last because it may contain '|'.</summary>
    public static RemoteSession? ParseTmuxLine(string line)
    {
        var parts = line.Split('|', 4);
        if (parts.Length < 4 || parts[3].Length == 0) return null;
        int.TryParse(parts[0], out int windows);
        int.TryParse(parts[1], out int attached);
        DateTimeOffset? created = long.TryParse(parts[2], out long seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        string detail = $"{windows} 个窗口" + (attached > 0 ? " · 已连接" : "");
        return new RemoteSession(RemoteSessionKind.Tmux, parts[3], windows, attached > 0, created, detail);
    }

    /// <summary>Parses tab-separated index, panes, active, command, session and window name.</summary>
    public static TmuxWindow? ParseTmuxWindow(string line)
    {
        var parts = line.Split('\t', 6);
        if (parts.Length < 6 || !int.TryParse(parts[0], out int index)) return null;
        int.TryParse(parts[1], out int panes);
        return new TmuxWindow(parts[4], index, parts[5], panes, parts[2] == "1", parts[3]);
    }

    /// <summary>Parses a row of <c>pct list</c> (VMID Status Lock Name) or <c>qm list</c> (VMID NAME STATUS MEM ...).</summary>
    public static PveGuest? ParsePveLine(string line, PveGuestKind kind)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || !int.TryParse(fields[0], out int id)) return null;
        if (kind == PveGuestKind.Lxc)
            // The lock column is blank most of the time, so the name is taken from the end.
            return new PveGuest(kind, id, fields.Length >= 3 ? fields[^1] : "CT" + id, fields[1], "LXC");
        if (fields.Length < 3) return null;
        string detail = fields.Length >= 4 && long.TryParse(fields[3], out long memory) ? $"VM · {memory} MB" : "VM";
        return new PveGuest(kind, id, fields[1], fields[2], detail);
    }

    /// <summary>Parses the output of <c>herdr session list --json</c>: {"sessions":[{name,default,running,...}]}.</summary>
    public static IReadOnlyList<RemoteSession> ParseHerdr(string json)
    {
        var sessions = new List<RemoteSession>();
        if (string.IsNullOrWhiteSpace(json)) return sessions;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sessions", out var list) || list.ValueKind != JsonValueKind.Array) return sessions;
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { Length: > 0 } name) continue;
                bool running = item.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True;
                bool isDefault = item.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True;
                string detail = (running ? "运行中" : "已停止") + (isDefault ? " · 默认" : "");
                sessions.Add(new RemoteSession(RemoteSessionKind.Herdr, name, 0, running, null, detail));
            }
        }
        catch (JsonException)
        {
            // Older herdr builds without --json print nothing usable; the session list stays empty.
        }
        return sessions;
    }

    /// <summary>Remote command that attaches to a session, creating it when missing.</summary>
    public static string AttachCommand(RemoteSessionKind kind, string name)
    {
        string inner = kind switch
        {
            RemoteSessionKind.Tmux when name.Length > 0 => "exec tmux new-session -A -s " + ShellQuote.Sh(name),
            RemoteSessionKind.Tmux => "tmux attach 2>/dev/null || exec tmux new-session",
            RemoteSessionKind.Herdr when name.Length > 0 => "exec herdr session attach " + ShellQuote.Sh(name),
            _ => "exec herdr",
        };
        // A login shell is needed because non-login PATHs often miss ~/.local/bin and Termux tools.
        return "sh -lc " + ShellQuote.Sh(inner);
    }

    /// <summary>Opens the best available shell inside a container on the remote host.</summary>
    public static string DockerExecCommand(string containerId, bool sudo = false, string user = "")
    {
        const string shell = "command -v bash >/dev/null && exec bash || exec sh";
        string userOption = user.Length > 0 ? "-u " + ShellQuote.Sh(user) + " " : "";
        return "sh -lc " + ShellQuote.Sh("exec " + (sudo ? "sudo " : "") + "docker exec -it " + userOption + ShellQuote.Sh(containerId) + " sh -c " + ShellQuote.Sh(shell));
    }

    public static string DockerLogsCommand(string containerId, bool sudo = false) =>
        "sh -lc " + ShellQuote.Sh("exec " + (sudo ? "sudo " : "") + "docker logs -f --tail 200 " + ShellQuote.Sh(containerId));

    public static string DockerActionCommand(string action, string containerId, bool sudo = false) => $"{(sudo ? "sudo -n " : "")}docker {action} {ShellQuote.Sh(containerId)}";

    public static string StopCommand(RemoteSessionKind kind, string name) => kind switch
    {
        RemoteSessionKind.Tmux => "tmux kill-session -t " + ShellQuote.Sh("=" + name),
        _ => "herdr session stop " + ShellQuote.Sh(name),
    };
}

/// <summary>tmux commands for the management UI. "=name" makes tmux match the session exactly, not by prefix.</summary>
public static class TmuxCommands
{
    private static string Session(string name) => ShellQuote.Sh("=" + name);
    private static string Window(string session, int index) => ShellQuote.Sh($"={session}:{index}");

    public static string Attach(string session, bool readOnly = false, bool detachOthers = false) =>
        "sh -lc " + ShellQuote.Sh("exec tmux attach-session" + (readOnly ? " -r" : "") + (detachOthers ? " -d" : "") + " -t " + Session(session));

    public static string AttachWindow(string session, int index) =>
        "sh -lc " + ShellQuote.Sh("exec tmux attach-session -t " + Window(session, index));

    public static string NewSession(string name) => "tmux new-session -d -s " + ShellQuote.Sh(name);
    public static string RenameSession(string name, string newName) => $"tmux rename-session -t {Session(name)} {ShellQuote.Sh(newName)}";
    public static string DetachClients(string session) => "tmux detach-client -s " + Session(session);
    public static string NewWindow(string session, string name) => $"tmux new-window -t {ShellQuote.Sh("=" + session + ":")}" + (name.Length > 0 ? " -n " + ShellQuote.Sh(name) : "");
    public static string RenameWindow(string session, int index, string newName) => $"tmux rename-window -t {Window(session, index)} {ShellQuote.Sh(newName)}";
    public static string KillWindow(string session, int index) => "tmux kill-window -t " + Window(session, index);

    /// <summary>tmux forbids ':' and '.' in session names and silently rewrites them, so they are refused up front.</summary>
    public static bool IsValidSessionName(string name) => name.Length > 0 && name.IndexOfAny(new[] { ':', '.' }) < 0;
}

/// <summary>Proxmox VE guest commands. Interactive ones use plain sudo so it can ask in the terminal.</summary>
public static class ProxmoxCommands
{
    private const string Path = "PATH=\"$PATH:/usr/sbin:/sbin\"; ";

    private static string Tool(PveGuestKind kind) => kind == PveGuestKind.Lxc ? "pct" : "qm";

    public static string Enter(PveGuest guest, bool sudo)
    {
        string prefix = sudo ? "sudo " : "";
        string inner = guest.Kind == PveGuestKind.Lxc
            ? $"exec {prefix}pct enter {guest.Id}"
            : $"echo '串口控制台需要虚拟机配置 serial0，并在系统内启用 getty。按 Ctrl+O 退出。'; exec {prefix}qm terminal {guest.Id}";
        return "sh -lc " + ShellQuote.Sh(Path + inner);
    }

    public static string Action(PveGuest guest, string action, bool sudo) => $"{Path}{(sudo ? "sudo -n " : "")}{Tool(guest.Kind)} {action} {guest.Id}";

    public static string AgentInterfaces(int vmId, bool sudo) => $"{Path}{(sudo ? "sudo -n " : "")}qm guest cmd {vmId} network-get-interfaces";

    /// <summary>Reads the first routable address out of a QEMU guest agent answer.</summary>
    public static string? ParseAgentAddress(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            string? fallback = null;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("ip-addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array) continue;
                foreach (var address in addresses.EnumerateArray())
                {
                    string ip = address.TryGetProperty("ip-address", out var a) ? a.GetString() ?? "" : "";
                    string type = address.TryGetProperty("ip-address-type", out var t) ? t.GetString() ?? "" : "";
                    if (ip.Length == 0 || ip.StartsWith("127.") || ip == "::1" || ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) || ip.StartsWith("169.254.")) continue;
                    if (type == "ipv4") return ip;
                    fallback ??= ip;
                }
            }
            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The noVNC console in the Proxmox web interface.</summary>
    public static string WebConsoleUrl(string host, string node, PveGuest guest) =>
        $"https://{host}:8006/?console={(guest.Kind == PveGuestKind.Lxc ? "lxc" : "kvm")}&novnc=1&vmid={guest.Id}&vmname={Uri.EscapeDataString(guest.Name)}&node={Uri.EscapeDataString(node)}&resize=scale";
}
