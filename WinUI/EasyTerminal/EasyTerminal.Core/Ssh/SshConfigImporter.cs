using EasyTerminal.Core.Models;

namespace EasyTerminal.Core.Ssh;

/// <summary>Turns the concrete Host blocks of an ssh_config file into devices.</summary>
public static class SshConfigImporter
{
    private sealed class Entry
    {
        public string Alias = "";
        public string Host = "";
        public string User = "";
        public int Port = 22;
        public string Key = "";
        public string Jump = "";
        public bool ForwardAgent;
    }

    /// <summary>Parses the file and returns devices new to <paramref name="existing"/>, wiring ProxyJump to other devices by alias.</summary>
    public static IReadOnlyList<Device> Parse(string text, IReadOnlyCollection<Device> existing)
    {
        var entries = new List<Entry>();
        List<Entry> current = new();
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(new[] { ' ', '\t', '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string key = parts[0].ToLowerInvariant();
            string value = parts[1].Trim().Trim('"');

            if (key == "match")
            {
                current = new();
                continue;
            }
            if (key == "host")
            {
                current = new();
                foreach (var pattern in value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Wildcards describe many machines, so they are defaults and not a device.
                    if (pattern.IndexOfAny(new[] { '*', '?', '!' }) >= 0) continue;
                    var entry = new Entry { Alias = pattern, Host = pattern };
                    current.Add(entry);
                    entries.Add(entry);
                }
                continue;
            }

            foreach (var entry in current)
            {
                switch (key)
                {
                    case "hostname": entry.Host = value; break;
                    case "user": entry.User = value; break;
                    case "port": if (int.TryParse(value, out int port)) entry.Port = port; break;
                    case "identityfile": if (entry.Key.Length == 0) entry.Key = ExpandHome(value); break;
                    case "proxyjump": entry.Jump = value; break;
                    case "forwardagent": entry.ForwardAgent = value.Equals("yes", StringComparison.OrdinalIgnoreCase); break;
                }
            }
        }

        var byAlias = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        var added = new List<Device>();
        foreach (var entry in entries)
        {
            var known = existing.FirstOrDefault(d => d.Host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase) && d.Port == entry.Port && d.User == entry.User);
            if (known != null)
            {
                byAlias[entry.Alias] = known;
                continue;
            }
            var device = new Device
            {
                Name = entry.Alias,
                Host = entry.Host,
                Port = entry.Port,
                User = entry.User,
                ForwardAgent = entry.ForwardAgent,
                Auth = entry.Key.Length > 0 ? AuthMethod.KeyFile : AuthMethod.Agent,
                KeyPath = entry.Key,
                Group = "SSH 配置",
            };
            device.Connections.Add(new Connection { Name = "SSH", Protocol = ConnectionProtocol.Ssh });
            device.Connections.Add(new Connection { Name = "SFTP", Protocol = ConnectionProtocol.Sftp });
            device.OfferBuiltInSessions();
            byAlias[entry.Alias] = device;
            added.Add(device);
        }

        foreach (var entry in entries.Where(e => e.Jump.Length > 0 && !e.Jump.Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            if (!byAlias.TryGetValue(entry.Alias, out var device) || !added.Contains(device)) continue;
            foreach (var hop in entry.Jump.Split(','))
            {
                // A hop may be written user@alias:port; only hops that are devices themselves can be linked.
                string alias = hop.Trim();
                int at = alias.LastIndexOf('@');
                if (at >= 0) alias = alias[(at + 1)..];
                int colon = alias.IndexOf(':');
                if (colon >= 0) alias = alias[..colon];
                if (byAlias.TryGetValue(alias, out var jump) && jump != device) device.Network.JumpDeviceIds.Add(jump.Id);
            }
        }
        return added;
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..].Replace('/', Path.DirectorySeparatorChar))
            : path;
}
