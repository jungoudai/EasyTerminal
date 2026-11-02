using System.Security.Cryptography;

namespace EasyTerminal.Core.Ssh;

public sealed record KnownHost(int LineNumber, string Hosts, string KeyType, string Fingerprint, bool IsHashed);

/// <summary>Reads and prunes OpenSSH's known_hosts file.</summary>
public static class KnownHostsFile
{
    public static string DefaultPath => Path.Combine(KeyManager.DefaultDirectory, "known_hosts");

    public static IReadOnlyList<KnownHost> Read(string? path = null)
    {
        path ??= DefaultPath;
        var entries = new List<KnownHost>();
        if (!File.Exists(path)) return entries;
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (ParseLine(lines[i], i) is { } entry) entries.Add(entry);
        return entries;
    }

    public static KnownHost? ParseLine(string line, int number)
    {
        string text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#')) return null;
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A leading @cert-authority or @revoked marker shifts the fields by one.
        int start = fields[0].StartsWith('@') ? 1 : 0;
        if (fields.Length < start + 3) return null;
        string hosts = fields[start];
        return new KnownHost(number, hosts, fields[start + 1], Fingerprint(fields[start + 2]), hosts.StartsWith("|1|", StringComparison.Ordinal));
    }

    /// <summary>The same SHA256 form that ssh prints when it asks to trust a host.</summary>
    public static string Fingerprint(string base64Key)
    {
        try
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(base64Key))).TrimEnd('=');
        }
        catch (FormatException)
        {
            return "";
        }
    }

    /// <summary>Removes one entry, checking the line still says what the caller saw in case ssh rewrote the file since.</summary>
    public static bool Remove(KnownHost entry, string? path = null)
    {
        path ??= DefaultPath;
        var lines = File.ReadAllLines(path).ToList();
        if (entry.LineNumber >= lines.Count || ParseLine(lines[entry.LineNumber], entry.LineNumber) != entry) return false;
        lines.RemoveAt(entry.LineNumber);
        string temp = path + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, path, true);
        return true;
    }
}
