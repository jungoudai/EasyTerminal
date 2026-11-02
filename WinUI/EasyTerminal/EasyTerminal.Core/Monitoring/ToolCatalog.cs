using EasyTerminal.Core.Ssh;

namespace EasyTerminal.Core.Monitoring;

public enum ToolCategory { Performance, Network, Disk, Containers }

/// <summary>A well-known terminal tool for watching or diagnosing a server.</summary>
/// <param name="Command">What to run; "{target}" is asked for when present.</param>
/// <param name="Package">Package name in common repositories; empty when it isn't packaged there.</param>
/// <param name="OneShot">Prints a report and exits, so a shell follows to keep the output readable.</param>
public sealed record Tool(string Name, ToolCategory Category, string Description, string Command, bool NeedsRoot = false, string Package = "", bool OneShot = false)
{
    public bool NeedsTarget => Command.Contains("{target}", StringComparison.Ordinal);
}

public static class ToolCatalog
{
    public static readonly IReadOnlyList<Tool> All = new Tool[]
    {
        new("htop", ToolCategory.Performance, "交互式进程查看器", "htop", Package: "htop"),
        new("btop", ToolCategory.Performance, "CPU、内存、磁盘、网络的全屏仪表盘", "btop", Package: "btop"),
        new("bpytop", ToolCategory.Performance, "btop 的 Python 前身", "bpytop"),
        new("glances", ToolCategory.Performance, "一屏看全系统状态", "glances", Package: "glances"),
        new("atop", ToolCategory.Performance, "按进程记录资源占用，可回放历史", "atop", Package: "atop"),
        new("nmon", ToolCategory.Performance, "IBM 的性能监视器", "nmon", Package: "nmon"),
        new("iotop", ToolCategory.Disk, "按进程看磁盘读写", "iotop -o", NeedsRoot: true, Package: "iotop"),
        new("ncdu", ToolCategory.Disk, "交互式查找占空间的目录", "ncdu /", Package: "ncdu"),
        new("duf", ToolCategory.Disk, "更易读的磁盘用量", "duf", Package: "duf", OneShot: true),
        new("iftop", ToolCategory.Network, "按连接看实时带宽", "iftop", NeedsRoot: true, Package: "iftop"),
        new("nload", ToolCategory.Network, "网卡流量曲线", "nload", Package: "nload"),
        new("nethogs", ToolCategory.Network, "按进程看网络流量", "nethogs", NeedsRoot: true, Package: "nethogs"),
        new("bmon", ToolCategory.Network, "带宽监视器", "bmon", Package: "bmon"),
        new("vnstat", ToolCategory.Network, "按天、按月的流量统计", "vnstat", Package: "vnstat", OneShot: true),
        new("mtr", ToolCategory.Network, "持续的路由追踪和丢包统计", "mtr {target}", Package: "mtr"),
        new("traceroute", ToolCategory.Network, "路由追踪", "traceroute {target}", Package: "traceroute", OneShot: true),
        new("speedtest", ToolCategory.Network, "Ookla 测速", "speedtest", OneShot: true),
        new("speedtest-cli", ToolCategory.Network, "命令行测速", "speedtest-cli", Package: "speedtest-cli", OneShot: true),
        new("iperf3", ToolCategory.Network, "两台机器之间的带宽测试", "iperf3 -c {target}", Package: "iperf3", OneShot: true),
        new("nmap", ToolCategory.Network, "端口扫描", "nmap {target}", Package: "nmap", OneShot: true),
        new("tcpdump", ToolCategory.Network, "抓包", "tcpdump -i any -nn -c 200", NeedsRoot: true, Package: "tcpdump"),
        new("ctop", ToolCategory.Containers, "容器资源的 top", "ctop"),
        new("lazydocker", ToolCategory.Containers, "Docker 的终端界面", "lazydocker"),
    };

    public static Tool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    public static string CategoryName(ToolCategory category) => category switch
    {
        ToolCategory.Performance => "性能",
        ToolCategory.Network => "网络",
        ToolCategory.Disk => "磁盘",
        _ => "容器",
    };

    /// <summary>Probe fragment that lists which of the tools are installed, and the package manager.</summary>
    public static string DetectScript =>
        "echo @@TOOLS; for t in " + string.Join(' ', All.Select(t => t.Name)) + "; do command -v $t >/dev/null 2>&1 && echo $t; done;" +
        "for m in apt-get dnf yum apk pacman zypper opkg pkg; do command -v $m >/dev/null 2>&1 && { echo \"pm=$m\"; break; }; done;";

    /// <summary>Remote command that opens a tool in a terminal, through sudo when it needs root and the login isn't.</summary>
    public static string LaunchCommand(Tool tool, string target, bool isRoot)
    {
        string command = tool.Command.Replace("{target}", ShellQuote.Sh(target));
        if (tool.NeedsRoot && !isRoot) command = "sudo " + command;
        // A report that prints and exits would vanish with its tab, so a shell stays behind to read it in.
        string script = tool.OneShot ? command + "; exec \"${SHELL:-sh}\" -l" : "exec " + command;
        return "sh -lc " + ShellQuote.Sh("PATH=\"$PATH:/usr/sbin:/sbin\"; " + script);
    }

    /// <summary>Remote command that installs a tool with the host's package manager, or null when that isn't possible.</summary>
    public static string? InstallCommand(Tool tool, string packageManager, bool isRoot)
    {
        if (tool.Package.Length == 0) return null;
        string package = ShellQuote.Sh(tool.Package);
        string? install = packageManager switch
        {
            "apt-get" => $"apt-get update && apt-get install -y {package}",
            "dnf" => $"dnf install -y {package}",
            "yum" => $"yum install -y epel-release; yum install -y {package}",
            "apk" => $"apk add {package}",
            "pacman" => $"pacman -S --noconfirm {package}",
            "zypper" => $"zypper install -y {package}",
            "opkg" => $"opkg update && opkg install {package}",
            "pkg" => $"pkg install -y {package}",
            _ => null,
        };
        if (install == null) return null;
        // Termux's pkg runs as the user; everywhere else installing takes root.
        string script = isRoot || packageManager == "pkg" ? install : $"sudo sh -c {ShellQuote.Sh(install)}";
        return "sh -lc " + ShellQuote.Sh(script + "; exec \"${SHELL:-sh}\" -l");
    }
}
