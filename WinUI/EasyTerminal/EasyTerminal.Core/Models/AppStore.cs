using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyTerminal.Core.Models;

public sealed class AppStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _writeLock = new();
    public AppData Data { get; private set; } = new();
    public SecretVault Secrets { get; }

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyTerminal");

    public AppStore(string? directory = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "config.json");
        Secrets = new SecretVault(Path.Combine(directory, "secrets.json"));
    }

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            Data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(_path), Json) ?? new AppData();
            // Devices saved before these became connections still have an SSH login to open them with.
            foreach (var device in Data.Devices)
            {
                device.MergeStatusSessions();
                if (device.Connections.Any(c => c.Protocol == ConnectionProtocol.Ssh)) device.OfferBuiltInSessions();
            }
        }
        catch (JsonException)
        {
            // Keep the unreadable file for recovery rather than overwriting it on the next save.
            File.Move(_path, _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            Data = new AppData();
        }
    }

    public void Save()
    {
        lock (_writeLock)
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, Json));
            File.Move(temp, _path, true);
        }
    }

    public Device? FindDevice(Guid id) => Data.Devices.FirstOrDefault(d => d.Id == id);

    /// <summary>Devices, snippets and forwards as a file to move elsewhere. Vault secrets stay behind.</summary>
    public string Export()
    {
        var snapshot = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(Data, Json), Json)!;
        foreach (var device in snapshot.Devices)
        {
            device.PasswordSecretId = null;
            device.Network.ProxySecretId = null;
            foreach (var connection in device.Connections)
            {
                connection.PasswordSecretId = null;
                connection.Network.ProxySecretId = null;
            }
        }
        snapshot.GitHubLogin = "";
        snapshot.GitHubTokenSecretId = null;
        snapshot.OpenSessions = new();
        snapshot.LocalLastUsed = new();
        return JsonSerializer.Serialize(snapshot, Json);
    }

    /// <summary>Adds what the file has and this machine lacks; existing entries always win. Returns the number added.</summary>
    public int Import(string json)
    {
        var incoming = JsonSerializer.Deserialize<AppData>(json, Json) ?? throw new JsonException("文件内容为空");
        int added = 0;
        foreach (var device in incoming.Devices.Where(d => FindDevice(d.Id) == null))
        {
            Data.Devices.Add(device);
            added++;
        }
        foreach (var snippet in incoming.Snippets.Where(s => Data.Snippets.All(e => e.Id != s.Id)))
        {
            Data.Snippets.Add(snippet);
            added++;
        }
        foreach (var forward in incoming.PortForwards.Where(f => Data.PortForwards.All(e => e.Id != f.Id)))
        {
            // Nothing from another machine may open ports on this one without being asked.
            forward.AutoStart = false;
            Data.PortForwards.Add(forward);
            added++;
        }
        return added;
    }

    public (Device Device, Connection Connection)? FindConnection(Guid id)
    {
        foreach (var device in Data.Devices)
            foreach (var connection in device.Connections)
                if (connection.Id == id) return (device, connection);
        return null;
    }
}

/// <summary>Stores passwords encrypted with DPAPI for the current Windows user.</summary>
public sealed class SecretVault
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _entries = new();

    public SecretVault(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            _entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException)
        {
            _entries = new();
        }
    }

    public string Set(string? id, string secret)
    {
        id ??= Guid.NewGuid().ToString("N");
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        lock (_lock)
        {
            _entries[id] = Convert.ToBase64String(encrypted);
            Persist();
        }
        return id;
    }

    public string? Get(string? id)
    {
        if (id == null) return null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var value)) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    public void Remove(string? id)
    {
        if (id == null) return;
        lock (_lock)
        {
            if (_entries.Remove(id)) Persist();
        }
    }

    private void Persist()
    {
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
        File.Move(temp, _path, true);
    }
}
