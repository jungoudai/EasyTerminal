using System.Text;

namespace EasyTerminal.Core.Rdp;

public sealed record RdpTarget(string Host, int Port, string User, string Domain, bool RedirectClipboard, bool RedirectDrives)
{
    /// <summary>host:port, with IPv6 literals bracketed.</summary>
    public string Address => (Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host) + (Port != 3389 ? ":" + Port : "");
}

/// <summary>Writes .rdp files for the system client, which is the fallback when embedding is not possible.</summary>
public static class RdpFile
{
    public static string Build(RdpTarget target)
    {
        var text = new StringBuilder();
        // Values run to the end of the line, so a stray line break could smuggle in another setting.
        void Add(string key, string value) => text.Append(key).Append(':').Append(value.ReplaceLineEndings("")).Append("\r\n");

        Add("full address:s", target.Address);
        if (target.User.Length > 0) Add("username:s", target.User);
        if (target.Domain.Length > 0) Add("domain:s", target.Domain);
        Add("redirectclipboard:i", target.RedirectClipboard ? "1" : "0");
        Add("drivestoredirect:s", target.RedirectDrives ? "*" : "");
        Add("screen mode id:i", "1");
        Add("smart sizing:i", "1");
        Add("dynamic resolution:i", "1");
        Add("session bpp:i", "32");
        return text.ToString();
    }
}
