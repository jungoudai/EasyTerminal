using System.Diagnostics;
using System.Text;

namespace EasyTerminal.Core.Ssh;

public enum KeyType { Ed25519Sk, EcdsaSk, Ed25519, Rsa4096, Ecdsa }

public sealed record SshKeyInfo(string Path, string PublicKey, string Type, string Fingerprint, string Comment, bool IsHardwareBacked);

public sealed class KeyGenerationOptions
{
    public string FileName { get; set; } = "id_ed25519_sk";
    public KeyType Type { get; set; } = KeyType.Ed25519Sk;
    public string Comment { get; set; } = "";
    public string Passphrase { get; set; } = "";
    /// <summary>Store the credential on the security key so it can be recovered on another machine.</summary>
    public bool Resident { get; set; }
    /// <summary>Require a PIN or biometric in addition to touch on every use.</summary>
    public bool VerifyRequired { get; set; }
    /// <summary>FIDO application string; must start with "ssh:" when set.</summary>
    public string Application { get; set; } = "";
    public string? Directory { get; set; }
}

/// <summary>Key management through ssh-keygen, which reaches FIDO2 hardware via Windows WebAuthn.</summary>
public static class KeyManager
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public static bool IsHardwareType(KeyType type) => type is KeyType.Ed25519Sk or KeyType.EcdsaSk;

    public static IReadOnlyList<string> BuildArguments(KeyGenerationOptions options, string path)
    {
        var args = new List<string> { "-q" };
        switch (options.Type)
        {
            case KeyType.Ed25519Sk: args.AddRange(new[] { "-t", "ed25519-sk" }); break;
            case KeyType.EcdsaSk: args.AddRange(new[] { "-t", "ecdsa-sk" }); break;
            case KeyType.Ed25519: args.AddRange(new[] { "-t", "ed25519" }); break;
            case KeyType.Rsa4096: args.AddRange(new[] { "-t", "rsa", "-b", "4096" }); break;
            case KeyType.Ecdsa: args.AddRange(new[] { "-t", "ecdsa", "-b", "521" }); break;
        }
        if (IsHardwareType(options.Type))
        {
            if (options.Resident) args.AddRange(new[] { "-O", "resident" });
            if (options.VerifyRequired) args.AddRange(new[] { "-O", "verify-required" });
            if (!string.IsNullOrWhiteSpace(options.Application)) args.AddRange(new[] { "-O", "application=" + options.Application });
        }
        args.AddRange(new[] { "-f", path, "-C", options.Comment });
        // Only the empty passphrase goes on the command line; a real one is answered through askpass
        // so it never shows up in a process listing.
        if (options.Passphrase.Length == 0) args.AddRange(new[] { "-N", "" });
        return args;
    }

    /// <summary>
    /// Runs ssh-keygen. Hardware keys wait for a touch, so the timeout is long and
    /// <paramref name="status"/> reports what the tool is asking for.
    /// </summary>
    public static async Task<SshKeyInfo> GenerateAsync(KeyGenerationOptions options, string askPassHelper, string askPassPipe, string token, CancellationToken cancellation = default)
    {
        string directory = options.Directory ?? DefaultDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, options.FileName);
        if (File.Exists(path) || File.Exists(path + ".pub"))
            throw new IOException($"{options.FileName} 已存在，请换一个文件名，以免覆盖现有密钥。");
        if (options.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("文件名包含无效字符。");

        var info = new ProcessStartInfo(SshRunner.SshKeygenPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in BuildArguments(options, path)) info.ArgumentList.Add(argument);
        info.Environment["SSH_ASKPASS"] = askPassHelper;
        info.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        info.Environment[SshHelperEntry.AskPassPipeVariable] = askPassPipe;
        info.Environment[SshHelperEntry.AskPassTokenVariable] = token;

        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 ssh-keygen");
        process.StandardInput.Close();
        var result = await ProcessRunner.CollectAsync(process, TimeSpan.FromMinutes(3), cancellation);
        if (result.ExitCode != 0 || !File.Exists(path + ".pub"))
        {
            string message = (result.Error + "\n" + result.Output).Trim();
            if (IsHardwareType(options.Type))
                message = "未能在安全密钥上创建凭据。请确认已插入支持 FIDO2 的密钥，并在提示时触摸。\n" + message;
            throw new IOException(message.Length > 0 ? message : $"ssh-keygen 退出代码 {result.ExitCode}");
        }
        return await ReadKeyAsync(path + ".pub");
    }

    public static async Task<IReadOnlyList<SshKeyInfo>> ListAsync(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return Array.Empty<SshKeyInfo>();
        var keys = new List<SshKeyInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.pub").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                keys.Add(await ReadKeyAsync(file));
            }
            catch (IOException)
            {
                // Files with a .pub extension that are not keys are simply not listed.
            }
        }
        return keys;
    }

    public static async Task<SshKeyInfo> ReadKeyAsync(string publicKeyPath)
    {
        string publicKey = (await File.ReadAllTextAsync(publicKeyPath)).Trim();
        var fields = publicKey.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || !(fields[0].StartsWith("ssh-") || fields[0].StartsWith("ecdsa-") || fields[0].StartsWith("sk-")))
            throw new IOException("不是有效的 SSH 公钥: " + publicKeyPath);

        string fingerprint = "";
        var result = await ProcessRunner.RunAsync(SshRunner.SshKeygenPath, new[] { "-lf", publicKeyPath }, TimeSpan.FromSeconds(10));
        if (result.ExitCode == 0)
        {
            var parts = result.Output.Trim().Split(' ');
            if (parts.Length >= 2) fingerprint = parts[1];
        }
        string privatePath = publicKeyPath[..^4];
        return new SshKeyInfo(privatePath, publicKey, fields[0], fingerprint, fields.Length > 2 ? fields[2] : "", fields[0].StartsWith("sk-"));
    }

    /// <summary>Remote script that appends a public key to authorized_keys if it is not already there.</summary>
    public static string DeployScript(string publicKey)
    {
        string key = ShellQuote.Sh(publicKey.Trim());
        return "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
               $"grep -qxF {key} ~/.ssh/authorized_keys || printf '%s\\n' {key} >> ~/.ssh/authorized_keys";
    }
}
