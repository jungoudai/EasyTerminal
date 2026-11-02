using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;

namespace EasyTerminal.Tests;

/// <summary>
/// Runs real ssh.exe and ssh-keygen.exe against the built app's helper modes. Skipped when the
/// app has not been built, because the helper lives in the WinUI executable.
/// </summary>
public class HelperIntegrationTests
{
    private static string? HelperPath()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "EasyTerminal.slnx"))) directory = Path.GetDirectoryName(directory);
        if (directory == null) return null;
        string path = Path.Combine(directory, "EasyTerminal", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "EasyTerminal.exe");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public async Task SshReachesTargetThroughProxyCommandHelper()
    {
        if (HelperPath() is not { } helper) return;

        // A SOCKS5 proxy that then pretends to be the SSH server, far enough to see the client's banner.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? requestedHost = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting);
            await stream.WriteAsync(new byte[] { 5, 0 });
            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            var host = new byte[head[4] + 2];
            await stream.ReadExactlyAsync(host);
            requestedHost = Encoding.ASCII.GetString(host, 0, head[4]);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await stream.WriteAsync("SSH-2.0-FakeServer\r\n"u8.ToArray());
            var buffer = new byte[256];
            int read = await stream.ReadAsync(buffer);
            return Encoding.ASCII.GetString(buffer, 0, read);
        });

        var device = new Device { Name = "t", Host = "target.invalid", User = "me", Network = new NetworkProfile { ProxyKind = ProxyKind.Socks5, ProxyHost = "127.0.0.1", ProxyPort = port } };
        var plan = new SshConfigBuilder(_ => null, _ => null, helper, null).Build(device, null);
        string config = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(config, plan.ConfigText, new UTF8Encoding(false));
        try
        {
            var info = new ProcessStartInfo(SshRunner.SshPath) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-F", config, "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", plan.Alias, "true" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var stderr = process.StandardError.ReadToEndAsync();
            string banner = await server.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("target.invalid", requestedHost);
            Assert.StartsWith("SSH-2.0-OpenSSH", banner);
            process.Kill(true);
            await process.WaitForExitAsync();
        }
        finally
        {
            File.Delete(config);
            listener.Stop();
        }
    }

    [Fact]
    public async Task KeygenPassphraseFlowsThroughAskPassPipe()
    {
        if (HelperPath() is not { } helper) return;
        string directory = Path.Combine(Path.GetTempPath(), "et-askpass-" + Guid.NewGuid().ToString("N"));
        var prompts = new List<string>();
        using var server = new AskPassServer(request =>
        {
            lock (prompts) prompts.Add(request.Prompt);
            return Task.FromResult<string?>("correct horse");
        });
        try
        {
            var options = new KeyGenerationOptions { FileName = "k", Type = KeyType.Ed25519, Passphrase = "correct horse", Comment = "t", Directory = directory };
            var key = await KeyManager.GenerateAsync(options, helper, server.PipeName, "token");
            Assert.Equal("ssh-ed25519", key.Type);
            Assert.NotEmpty(prompts);
            Assert.All(prompts, p => Assert.Contains("passphrase", p, StringComparison.OrdinalIgnoreCase));

            // The key must really be encrypted: wrong passphrase fails, right one recovers the public key.
            var wrong = await ProcessRunner.RunAsync(SshRunner.SshKeygenPath, new[] { "-y", "-P", "nope", "-f", key.Path }, TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, wrong.ExitCode);
            var right = await ProcessRunner.RunAsync(SshRunner.SshKeygenPath, new[] { "-y", "-P", "correct horse", "-f", key.Path }, TimeSpan.FromSeconds(10));
            Assert.Equal(0, right.ExitCode);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
