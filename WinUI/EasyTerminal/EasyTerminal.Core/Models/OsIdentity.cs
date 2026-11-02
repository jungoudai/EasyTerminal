namespace EasyTerminal.Core.Models;

/// <summary>Which bundled logo stands for a machine, from what a scan reported.</summary>
public static class OsIdentity
{
    // Derivatives come before the systems they are built on, since their names often mention the base.
    private static readonly (string Match, string Logo)[] Names =
    {
        ("pop!_os", "popos"), ("zorin", "zorin"), ("elementary", "elementary"), ("linux mint", "linuxmint"),
        ("kali", "kalilinux"), ("raspbian", "raspberrypi"), ("mx linux", "mxlinux"), ("deepin", "deepin"),
        ("endeavour", "endeavouros"), ("manjaro", "manjaro"), ("cachyos", "cachyos"), ("tails", "tails"), ("qubes", "qubesos"),
        ("truenas", "truenas"), ("unraid", "unraid"), ("synology", "synology"), ("qnap", "qnap"), ("openwrt", "openwrt"),
        ("ubuntu", "ubuntu"), ("debian", "debian"), ("centos", "centos"), ("fedora", "fedora"), ("rocky", "rockylinux"),
        ("alma", "almalinux"), ("red hat", "redhat"), ("opensuse", "opensuse"), ("suse", "suse"), ("arch linux", "archlinux"),
        ("alpine", "alpinelinux"), ("gentoo", "gentoo"), ("nixos", "nixos"), ("void", "voidlinux"), ("slackware", "slackware"),
        ("freebsd", "freebsd"), ("openbsd", "openbsd"), ("macos", "apple"), ("mac os", "apple"), ("android", "android"),
        ("windows", "windows"),
    };

    /// <summary>The logo's file name without extension, or null when nothing is known about the system.</summary>
    public static string? LogoFor(DeviceFacts? facts)
    {
        if (facts == null) return null;
        if (facts.ProxmoxVersion.Length > 0) return "proxmox";
        string os = facts.Os.ToLowerInvariant();
        foreach (var (match, logo) in Names)
            if (os.Contains(match, StringComparison.Ordinal)) return logo;
        string kernel = facts.Kernel.ToLowerInvariant();
        if (kernel.StartsWith("darwin", StringComparison.Ordinal)) return "apple";
        if (kernel.StartsWith("freebsd", StringComparison.Ordinal)) return "freebsd";
        if (kernel.StartsWith("openbsd", StringComparison.Ordinal)) return "openbsd";
        return kernel.StartsWith("linux", StringComparison.Ordinal) || os.Contains("linux", StringComparison.Ordinal) ? "linux" : null;
    }
}
