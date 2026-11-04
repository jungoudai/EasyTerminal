using System.Text.Json.Serialization;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Terminal;

namespace EasyTerminal.Core.Models;

public enum ConnectionProtocol { Ssh, Sftp, Mosh, Tmux, Herdr, Rdp, Telnet, Serial, Docker, Proxmox, Monitor, Tools, Network }
public enum AuthMethod { Agent, KeyFile, Password }
/// <summary>Tailscale routes through the built-in node instead of a proxy server the user runs.</summary>
public enum ProxyKind { None, Socks5, Socks4, Http, Tailscale }
public enum LocalKind { Shell, Wsl, Docker, Multipass }
public enum PortForwardKind { Local, Remote, Dynamic }

public sealed class NetworkProfile
{
    public ProxyKind ProxyKind { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 1080;
    public string ProxyUser { get; set; } = "";
    /// <summary>Stable id of the proxy password in the vault; null when none is saved.</summary>
    public string? ProxySecretId { get; set; }
    /// <summary>Hops in order from this machine towards the target.</summary>
    public List<Guid> JumpDeviceIds { get; set; } = new();

    [JsonIgnore]
    public bool IsDirect => ProxyKind == ProxyKind.None && JumpDeviceIds.Count == 0;
}

public sealed class Connection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ConnectionProtocol Protocol { get; set; }
    /// <summary>tmux or herdr session name; empty means the default session.</summary>
    public string SessionName { get; set; } = "";
    /// <summary>Command run on the remote instead of a login shell (SSH), or the start directory (SFTP).</summary>
    public string Command { get; set; } = "";
    /// <summary>Overrides for this connection; empty values fall back to the device.</summary>
    public string User { get; set; } = "";
    public int? Port { get; set; }
    public bool OverrideNetwork { get; set; }
    public NetworkProfile Network { get; set; } = new();
    public DateTimeOffset? LastUsed { get; set; }
    /// <summary>Snippet run when an SSH session opens, before the login shell takes over.</summary>
    public Guid? StartupSnippetId { get; set; }
    /// <summary>RDP sign-in domain; empty means none.</summary>
    public string Domain { get; set; } = "";
    /// <summary>Stable id of the RDP password in the vault; null makes the server or client ask.</summary>
    public string? PasswordSecretId { get; set; }
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    /// <summary>Serial line speed; the port itself is the device's host, such as COM3.</summary>
    public int BaudRate { get; set; } = 115200;
}

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Other addresses of the same machine, tried in order after <see cref="Host"/>; each may include a port.</summary>
    public List<string> AlternateAddresses { get; set; } = new();
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public AuthMethod Auth { get; set; } = AuthMethod.Agent;
    public string KeyPath { get; set; } = "";
    public string? PasswordSecretId { get; set; }
    public NetworkProfile Network { get; set; } = new();
    public List<Connection> Connections { get; set; } = new();
    public bool IsExpanded { get; set; } = true;
    /// <summary>Folder shown on the devices page, nested with "/" such as 生产/上海; empty means ungrouped.</summary>
    public string Group { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool ForwardAgent { get; set; }
    /// <summary>What the last scan learned, kept so the card is informative before the next scan.</summary>
    public DeviceFacts? Facts { get; set; }
    /// <summary>Sessions this device was offered automatically, so one the user deleted is not offered again.</summary>
    public List<ConnectionProtocol> OfferedConnections { get; set; } = new();

    /// <summary>Adds a session the first time it applies to this device, unless one like it already exists.</summary>
    public void Offer(ConnectionProtocol protocol, string name)
    {
        if (OfferedConnections.Contains(protocol)) return;
        OfferedConnections.Add(protocol);
        bool exists = Connections.Any(c => c.Protocol == protocol && (protocol != ConnectionProtocol.Tmux || c.SessionName.Length == 0));
        if (!exists) Connections.Add(new Connection { Name = name, Protocol = protocol });
    }

    /// <summary>The status view (performance, network, tools) only needs a shell, so every SSH machine gets it.</summary>
    public void OfferBuiltInSessions() => Offer(ConnectionProtocol.Monitor, "状态");

    /// <summary>
    /// 1.0.5 offered network and tools as sessions of their own and called the status view "性能".
    /// Those are one view now, so the extra buttons go and the old default name follows.
    /// </summary>
    public void MergeStatusSessions()
    {
        Connections.RemoveAll(c => c.Protocol is ConnectionProtocol.Network or ConnectionProtocol.Tools);
        foreach (var connection in Connections.Where(c => c.Protocol == ConnectionProtocol.Monitor && c.Name == "性能"))
            connection.Name = "状态";
    }

    /// <summary>Lists of tmux sessions, containers or guests only make sense once a scan found that software.</summary>
    public bool Shows(Connection connection) => connection.Protocol switch
    {
        ConnectionProtocol.Tmux when connection.SessionName.Length == 0 => Facts?.HasTmux == true,
        ConnectionProtocol.Docker => Facts?.HasDocker == true,
        ConnectionProtocol.Proxmox => Facts?.HasProxmox == true,
        ConnectionProtocol.Tools or ConnectionProtocol.Network => false,
        _ => true,
    };
}

/// <summary>Operating system, load and location of a machine at the time of its last scan.</summary>
public sealed class DeviceFacts
{
    public string Hostname { get; set; } = "";
    public string Os { get; set; } = "";
    public string Kernel { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Virtualization { get; set; } = "";
    public string ProxmoxVersion { get; set; } = "";
    public int CpuCount { get; set; }
    public long MemoryTotalKb { get; set; }
    public long MemoryAvailableKb { get; set; }
    public double Load1 { get; set; }
    public double Load5 { get; set; }
    public double Load15 { get; set; }
    public long UptimeSeconds { get; set; }
    public string PublicIp { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Region { get; set; } = "";
    public string City { get; set; } = "";
    public string Network { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }
    public bool HasTmux { get; set; }
    public bool HasDocker { get; set; }
    public bool HasProxmox { get; set; }
    /// <summary>Known monitoring and diagnostic tools found on the machine.</summary>
    public List<string> Tools { get; set; } = new();
    public string PackageManager { get; set; } = "";
    public bool IsRoot { get; set; }
    /// <summary>Proxmox node name and guests, kept so discovered VMs and containers show before the next scan.</summary>
    public string ProxmoxNode { get; set; } = "";
    public bool ProxmoxNeedsSudo { get; set; }
    public List<PveGuest> Guests { get; set; } = new();
}

/// <summary>Device folders are paths such as 生产/上海; these keep them tidy and answer what lies inside what.</summary>
public static class FolderPath
{
    public const char Separator = '/';

    /// <summary>Trims each level and drops empty ones, so " a / /b " becomes "a/b".</summary>
    public static string Normalize(string? path) =>
        string.Join(Separator, (path ?? "").Split(new[] { '/', '\\' }).Select(p => p.Trim()).Where(p => p.Length > 0));

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies below it; every path is within the root "".</summary>
    public static bool IsWithin(string path, string folder) =>
        folder.Length == 0 || path == folder || path.StartsWith(folder + Separator, StringComparison.Ordinal);

    public static string Parent(string path)
    {
        int slash = path.LastIndexOf(Separator);
        return slash < 0 ? "" : path[..slash];
    }

    public static string Leaf(string path) => path[(path.LastIndexOf(Separator) + 1)..];

    public static string Combine(string parent, string child) => Normalize(parent.Length == 0 ? child : parent + Separator + child);

    /// <summary>The folder and every folder above it, outermost first.</summary>
    public static IEnumerable<string> SelfAndAncestors(string path)
    {
        if (path.Length == 0) yield break;
        for (int slash = path.IndexOf(Separator); slash >= 0; slash = path.IndexOf(Separator, slash + 1))
            yield return path[..slash];
        yield return path;
    }

    /// <summary>Moves <paramref name="path"/> along when the folder it is in, or one above it, is renamed.</summary>
    public static string Rename(string path, string from, string to) =>
        from.Length > 0 && IsWithin(path, from) ? Normalize(to + path[from.Length..]) : path;
}

/// <summary>A saved piece of text sent to terminals; {{name}} placeholders are asked for when it runs.</summary>
public sealed class Snippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public string Group { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Press Enter after sending; off leaves the text at the prompt to review.</summary>
    public bool RunImmediately { get; set; } = true;
    public DateTimeOffset? LastUsed { get; set; }
}

public sealed class PortForward
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public PortForwardKind Kind { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>Loopback unless the user chooses to expose the port.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; }
    public bool AutoStart { get; set; }
}

/// <summary>A runnable local environment found by a scan or pinned as a shell.</summary>
public sealed record LocalTarget(LocalKind Kind, string Id, string Name, string Detail, bool IsRunning, IReadOnlyList<string> Command)
{
    public string StableKey => $"{Kind}:{Id}";
}

public sealed class AppSettings
{
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 13;
    public double LineHeight { get; set; } = 1.15;
    public string ColorScheme { get; set; } = "One Half Dark";
    public int Scrollback { get; set; } = 10000;
    public bool CursorBlink { get; set; } = true;
    public string Theme { get; set; } = "Default";
    public string DefaultShellKey { get; set; } = "";
    public bool RestoreSessions { get; set; }
    public bool ConfirmCloseRunning { get; set; } = true;
    public bool BoldIsBright { get; set; } = true;
    public string AssistantUrl { get; set; } = "https://chat.deepseek.com/";
    public double AssistantWidth { get; set; } = 440;
    /// <summary>Pinned side panels take room from the terminal; floating ones lie on top so it keeps its size.</summary>
    public bool AssistantPinned { get; set; } = true;
    public bool SnippetsPinned { get; set; }
    public double SnippetsWidth { get; set; } = 360;
    /// <summary>The AI panel shows the built-in agent; off shows the web page at <see cref="AssistantUrl"/>.</summary>
    public bool AssistantUsesAgent { get; set; } = true;
    public AgentSettings Agent { get; set; } = new();
    /// <summary>Lets a scan ask ipinfo.io or ipapi.co where the machine is.</summary>
    public bool RegionLookup { get; set; } = true;
    public bool LogSessions { get; set; }
    public TailscaleSettings Tailscale { get; set; } = new();
    /// <summary>Color schemes the user imported or edited; they appear beside the built-in ones.</summary>
    public List<TerminalSchemeData> CustomSchemes { get; set; } = new();
    /// <summary>Terminal background opacity in percent. Below 100 the window's material (Mica or acrylic) shows through.</summary>
    public int TerminalOpacity { get; set; } = 100;
    /// <summary>Window material: Mica, MicaAlt or Acrylic.</summary>
    public string WindowBackdrop { get; set; } = "Mica";
    /// <summary>What the home page shows, in order; null means the default layout.</summary>
    public List<HomeWidget>? HomeWidgets { get; set; }
}

public enum HomeWidgetKind { Overview, Sessions, Devices, Recent, Resources, LiveMonitor, Forwards, Environment, LocalShells, Tailscale }

/// <summary>One panel on the home page. The page is twelve columns wide; narrow windows widen panels to fit.</summary>
public sealed class HomeWidget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public HomeWidgetKind Kind { get; set; }
    /// <summary>Columns out of twelve: 3, 4, 6, 8 or 12.</summary>
    public int Width { get; set; } = 6;
    /// <summary>Replaces the default title when not empty.</summary>
    public string Title { get; set; } = "";
    /// <summary>Device folder to show for device and resource panels; empty shows every device.</summary>
    public string Group { get; set; } = "";
    /// <summary>The machine a live monitor samples.</summary>
    public Guid? DeviceId { get; set; }
    /// <summary>Longest list a panel shows.</summary>
    public int MaxItems { get; set; } = 8;

    public static List<HomeWidget> DefaultLayout() => new()
    {
        new HomeWidget { Kind = HomeWidgetKind.Overview, Width = 12 },
        new HomeWidget { Kind = HomeWidgetKind.Sessions, Width = 8 },
        new HomeWidget { Kind = HomeWidgetKind.Recent, Width = 4, MaxItems = 6 },
        new HomeWidget { Kind = HomeWidgetKind.Devices, Width = 12 },
        new HomeWidget { Kind = HomeWidgetKind.Resources, Width = 8 },
        new HomeWidget { Kind = HomeWidgetKind.LocalShells, Width = 4 },
        new HomeWidget { Kind = HomeWidgetKind.Environment, Width = 12 },
    };
}

/// <summary>The built-in Tailscale node, which devices use by picking Tailscale as their proxy.</summary>
public sealed class TailscaleSettings
{
    /// <summary>Starts the node with the app; otherwise it starts the first time a connection needs it.</summary>
    public bool StartWithApp { get; set; }
    /// <summary>Name of this machine in the tailnet; empty uses easyterminal-&lt;computer name&gt;.</summary>
    public string Hostname { get; set; } = "";
    /// <summary>Coordination server; empty is Tailscale's own. Set it to use Headscale.</summary>
    public string ControlUrl { get; set; } = "";
    /// <summary>Stable id of a pre-authentication key in the vault; null signs in through the browser.</summary>
    public string? AuthKeySecretId { get; set; }
    /// <summary>Removes the node from the tailnet when the app closes.</summary>
    public bool Ephemeral { get; set; }
}

public enum AgentProvider { OpenAICompatible, Anthropic }

/// <summary>The model behind the built-in agent. Most providers speak the OpenAI chat API at their own address.</summary>
public sealed class AgentSettings
{
    public AgentProvider Provider { get; set; } = AgentProvider.OpenAICompatible;
    public string Endpoint { get; set; } = "https://api.deepseek.com/v1";
    public string Model { get; set; } = "deepseek-chat";
    /// <summary>Stable id of the API key in the vault; null when none is saved.</summary>
    public string? ApiKeySecretId { get; set; }
    /// <summary>Lets the agent run commands without asking first. Off by default.</summary>
    public bool AutoApproveCommands { get; set; }
}

/// <summary>Enough to reopen a tab on the next launch.</summary>
public sealed class SessionRef
{
    public Guid? ConnectionId { get; set; }
    public string? LocalKey { get; set; }
}

public sealed class AppData
{
    public List<Device> Devices { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public List<SessionRef> OpenSessions { get; set; } = new();
    public Dictionary<string, DateTimeOffset> LocalLastUsed { get; set; } = new();
    public List<Snippet> Snippets { get; set; } = new();
    public List<PortForward> PortForwards { get; set; } = new();
    public string GitHubLogin { get; set; } = "";
    /// <summary>Stable id of the GitHub token in the vault; null when not connected.</summary>
    public string? GitHubTokenSecretId { get; set; }
    /// <summary>Device folders the user created, kept even while empty. Folders that devices name exist anyway.</summary>
    public List<string> DeviceFolders { get; set; } = new();
    /// <summary>Folders and sections folded on the devices page.</summary>
    public List<string> CollapsedFolders { get; set; } = new();

    /// <summary>Every device folder, parents included, in display order.</summary>
    public List<string> AllDeviceFolders() =>
        DeviceFolders.Concat(Devices.Select(d => d.Group))
            .SelectMany(FolderPath.SelfAndAncestors)
            .Distinct()
            .OrderBy(f => f, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Every tag in use with how many devices carry it, most used first.</summary>
    public List<(string Tag, int Count)> AllDeviceTags() =>
        Devices.SelectMany(d => d.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First(), g.Count()))
            .OrderByDescending(t => t.Item2).ThenBy(t => t.Item1, StringComparer.CurrentCulture)
            .ToList();
}
