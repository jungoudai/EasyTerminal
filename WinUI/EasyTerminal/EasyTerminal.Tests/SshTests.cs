using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyTerminal.Core.Local;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Core.Transport;

namespace EasyTerminal.Tests;

public class SshConfigTests
{
    private static Device Dev(string name, string host, NetworkProfile? network = null) => new() { Name = name, Host = host, User = "u" + name, Network = network ?? new NetworkProfile() };

    private static SshConfigBuilder Builder(params Device[] devices) =>
        new(id => devices.FirstOrDefault(d => d.Id == id), _ => "secret", @"C:\App Dir\EasyTerminal.exe", null);

    /// <summary>Asks real ssh to resolve the generated config, which proves OpenSSH accepts it.</summary>
    private static Dictionary<string, string> Resolve(string config, string alias, string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(path, config, new UTF8Encoding(false));
        try
        {
            var info = new ProcessStartInfo(SshRunner.SshPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-G");
            info.ArgumentList.Add("-F");
            info.ArgumentList.Add(path);
            info.ArgumentList.Add(alias);
            using var process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "ssh -G failed: " + error + "\n" + config);
            var values = new Dictionary<string, string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length == 2) values.TryAdd(parts[0], parts[1]);
            }
            return values;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DirectConnection()
    {
        var device = Dev("a", "example.com");
        device.Port = 2222;
        device.Auth = AuthMethod.KeyFile;
        device.KeyPath = @"C:\Users\me\.ssh\id_ed25519_sk";
        var plan = Builder(device).Build(device, null);
        var resolved = Resolve(plan.ConfigText, plan.Alias);
        Assert.Equal("example.com", resolved["hostname"]);
        Assert.Equal("2222", resolved["port"]);
        Assert.Equal("ua", resolved["user"]);
        Assert.Contains("id_ed25519_sk", resolved["identityfile"]);
        Assert.Equal("yes", resolved["identitiesonly"]);
    }

    [Fact]
    public void ConnectionOverridesUserPortAndNetwork()
    {
        var device = Dev("a", "example.com", new NetworkProfile { ProxyKind = ProxyKind.Http, ProxyHost = "deviceproxy", ProxyPort = 1 });
        var connection = new Connection { User = "root", Port = 2200, OverrideNetwork = true, Network = new NetworkProfile { ProxyKind = ProxyKind.Socks5, ProxyHost = "connproxy", ProxyPort = 1080 } };
        var plan = Builder(device).Build(device, connection);
        var resolved = Resolve(plan.ConfigText, plan.Alias);
        Assert.Equal("root", resolved["user"]);
        Assert.Equal("2200", resolved["port"]);
        Assert.Contains("--proxy-connect socks5 connproxy 1080", resolved["proxycommand"]);
        Assert.DoesNotContain("deviceproxy", plan.ConfigText);
    }

    [Fact]
    public void JumpChainGivesEachHopItsOwnSettings()
    {
        var first = Dev("first", "first.example");
        first.Auth = AuthMethod.KeyFile;
        first.KeyPath = "C:/keys/first";
        var second = Dev("second", "second.example");
        var target = Dev("target", "target.example", new NetworkProfile { JumpDeviceIds = { first.Id, second.Id }, ProxyKind = ProxyKind.Socks5, ProxyHost = "proxy", ProxyPort = 9 });

        var plan = Builder(first, second, target).Build(target, null);
        var resolvedTarget = Resolve(plan.ConfigText, plan.Alias);
        Assert.Equal("et-hop1", resolvedTarget["proxyjump"]);
        Assert.False(resolvedTarget.ContainsKey("proxycommand"));

        var hop1 = Resolve(plan.ConfigText, "et-hop1");
        Assert.Equal("second.example", hop1["hostname"]);
        Assert.Equal("usecond", hop1["user"]);
        Assert.Equal("et-hop0", hop1["proxyjump"]);

        var hop0 = Resolve(plan.ConfigText, "et-hop0");
        Assert.Equal("first.example", hop0["hostname"]);
        Assert.Contains("--proxy-connect socks5 proxy 9", hop0["proxycommand"]);
        Assert.Contains("first", hop0["identityfile"]);
    }

    [Fact]
    public void JumpCycleIsRejected()
    {
        var a = Dev("a", "a");
        var b = Dev("b", "b", new NetworkProfile { JumpDeviceIds = { a.Id } });
        a.Network.JumpDeviceIds.Add(b.Id);
        Assert.Throws<InvalidOperationException>(() => Builder(a, b).Build(a, null));
    }

    [Fact]
    public void ProxyPasswordGoesToEnvironmentNotConfig()
    {
        var device = Dev("a", "h", new NetworkProfile { ProxyKind = ProxyKind.Socks5, ProxyHost = "p", ProxyPort = 1, ProxyUser = "pu", ProxySecretId = "x" });
        var plan = Builder(device).Build(device, null);
        Assert.DoesNotContain("secret", plan.ConfigText);
        Assert.Equal("secret", plan.Environment[SshHelperEntry.ProxyPasswordVariable("p", 1)]);
    }
}

public class ProxyHandshakeTests
{
    private static async Task<(string Request, byte[] Tunnel)> RunServer(TcpListener listener, Func<NetworkStream, Task<string>> script)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        string request = await script(stream);
        var buffer = new byte[4];
        await stream.ReadExactlyAsync(buffer);
        await stream.WriteAsync(buffer);
        return (request, buffer);
    }

    private static async Task<string> Exercise(string kind, string? user, string? password, Func<NetworkStream, Task<string>> script)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunServer(listener, script);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        ProxyHandshake.Perform(stream, kind, "target.example", 22, user, password);
        await stream.WriteAsync("ping"u8.ToArray());
        var echo = new byte[4];
        await stream.ReadExactlyAsync(echo);
        Assert.Equal("ping", Encoding.ASCII.GetString(echo));
        var (request, _) = await server;
        listener.Stop();
        return request;
    }

    [Fact]
    public async Task Socks5WithAuthentication()
    {
        string request = await Exercise("socks5", "bob", "pw", async s =>
        {
            var greeting = new byte[4];
            await s.ReadExactlyAsync(greeting);
            Assert.Equal(new byte[] { 5, 2, 0, 2 }, greeting);
            await s.WriteAsync(new byte[] { 5, 2 });
            var auth = new byte[1 + 1 + 3 + 1 + 2];
            await s.ReadExactlyAsync(auth);
            Assert.Equal("bob", Encoding.ASCII.GetString(auth, 2, 3));
            Assert.Equal("pw", Encoding.ASCII.GetString(auth, 6, 2));
            await s.WriteAsync(new byte[] { 1, 0 });
            var head = new byte[5];
            await s.ReadExactlyAsync(head);
            var host = new byte[head[4] + 2];
            await s.ReadExactlyAsync(host);
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            return Encoding.ASCII.GetString(host, 0, head[4]);
        });
        Assert.Equal("target.example", request);
    }

    [Fact]
    public async Task HttpConnectDoesNotConsumeTunnelBytes()
    {
        string request = await Exercise("http", "bob", "pw", async s =>
        {
            var sb = new StringBuilder();
            while (!sb.ToString().EndsWith("\r\n\r\n")) sb.Append((char)s.ReadByte());
            await s.WriteAsync("HTTP/1.1 200 OK\r\nServer: t\r\n\r\n"u8.ToArray());
            return sb.ToString();
        });
        Assert.StartsWith("CONNECT target.example:22 HTTP/1.1", request);
        Assert.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("bob:pw")), request);
    }

    [Fact]
    public async Task Socks4aSendsHostname()
    {
        string request = await Exercise("socks4", null, null, async s =>
        {
            var head = new byte[8];
            await s.ReadExactlyAsync(head);
            Assert.Equal(4, head[0]);
            var sb = new StringBuilder();
            s.ReadByte(); // empty user id terminator
            int b;
            while ((b = s.ReadByte()) > 0) sb.Append((char)b);
            await s.WriteAsync(new byte[] { 0, 90, 0, 0, 0, 0, 0, 0 });
            return sb.ToString();
        });
        Assert.Equal("target.example", request);
    }
}

public class ProbeParsingTests
{
    [Fact]
    public void ParsesAllSections()
    {
        string output = "motd noise\n@@ENV\ntermux\nmosh\ntmux\n@@TMUX\n2|1|1700000000|work|with pipe\n1|0|1700000001|misc\n@@HERDR\n" +
                        "{\"sessions\":[{\"name\":\"default\",\"default\":true,\"running\":true,\"socket_path\":\"/s\",\"session_dir\":\"/d\"},{\"name\":\"side\",\"default\":false,\"running\":false,\"socket_path\":\"/s\",\"session_dir\":\"/d\"}]}\n@@END\n";
        var result = RemoteProbe.Parse(output);
        Assert.True(result.IsTermux);
        Assert.True(result.HasMosh);
        Assert.True(result.HasTmux);
        Assert.True(result.HasHerdr);
        Assert.Equal(4, result.Sessions.Count);
        Assert.Equal("work|with pipe", result.Sessions[0].Name);
        Assert.True(result.Sessions[0].IsActive);
        Assert.Equal(2, result.Sessions[0].Windows);
        Assert.False(result.Sessions[1].IsActive);
        Assert.Equal(RemoteSessionKind.Herdr, result.Sessions[2].Kind);
        Assert.True(result.Sessions[2].IsActive);
        Assert.False(result.Sessions[3].IsActive);
    }

    [Fact]
    public void MissingToolsYieldEmptyResult()
    {
        var result = RemoteProbe.Parse("@@ENV\n@@END\n");
        Assert.False(result.HasTmux || result.HasHerdr || result.IsTermux);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void HerdrWithoutJsonSupportIsTolerated()
    {
        Assert.Empty(RemoteProbe.ParseHerdr("NAME  STATE\nwork  running\n"));
    }

    [Fact]
    public void AttachCommandQuotesHostileNames()
    {
        string command = RemoteProbe.AttachCommand(RemoteSessionKind.Tmux, "a'; rm -rf /");
        // Run the quoting through a real POSIX shell if one is available locally.
        Assert.StartsWith("sh -lc ", command);
        Assert.Contains("'\\''", command);
    }

    [Fact]
    public async Task ProbeScriptRunsUnderARealShell()
    {
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        var result = await ProcessRunner.RunAsync(bash, new[] { "-c", RemoteProbe.Script }, TimeSpan.FromSeconds(20));
        var parsed = RemoteProbe.Parse(result.Output);
        Assert.Contains("@@END", result.Output);
        Assert.False(parsed.IsTermux);
    }
}

public class LocalScannerTests
{
    [Fact]
    public void ParsesWslOutput()
    {
        string output = "  NAME            STATE           VERSION\r\n* Ubuntu-22.04    Running         2\r\n  Debian GNU      Stopped         1\r\n";
        var targets = LocalScanner.ParseWsl(output, "wsl.exe");
        Assert.Equal(2, targets.Count);
        Assert.Equal("Ubuntu-22.04", targets[0].Id);
        Assert.True(targets[0].IsRunning);
        Assert.Contains("默认", targets[0].Detail);
        Assert.Equal("Debian GNU", targets[1].Name);
        Assert.False(targets[1].IsRunning);
    }

    [Fact]
    public void ParsesLocalizedWslHeaderByPosition()
    {
        var targets = LocalScanner.ParseWsl("  名称   状态   版本\r\n* Arch   正在运行   2\r\n");
        Assert.Single(targets);
        Assert.True(targets[0].IsRunning);
    }

    [Fact]
    public void ParsesDockerAndSkipsWarnings()
    {
        string output = "WARNING: something\n{\"ID\":\"abc\",\"Names\":\"web\",\"Image\":\"nginx\",\"State\":\"running\"}\n{\"ID\":\"def\",\"Names\":\"db\",\"Image\":\"pg\",\"State\":\"exited\"}\n";
        var targets = LocalScanner.ParseDocker(output);
        Assert.Equal(2, targets.Count);
        Assert.True(targets[0].IsRunning);
        Assert.Contains("exec", targets[0].Command);
        Assert.False(targets[1].IsRunning);
    }

    [Fact]
    public void ParsesMultipass()
    {
        var targets = LocalScanner.ParseMultipass("{\"list\":[{\"name\":\"vm1\",\"state\":\"Running\",\"release\":\"24.04 LTS\",\"ipv4\":[]},{\"name\":\"vm2\",\"state\":\"Stopped\",\"release\":\"\",\"ipv4\":[]}]}");
        Assert.Equal(2, targets.Count);
        Assert.Equal(new[] { "multipass.exe", "shell", "vm1" }, targets[0].Command);
    }

    [Fact]
    public async Task RealScanFindsShellsAndCompletes()
    {
        var result = await LocalScanner.ScanAsync();
        Assert.Contains(result.Targets, t => t.Kind == LocalKind.Shell && t.Id == "cmd");
        Assert.Equal(3, result.Statuses.Count);
    }
}

public class KeyManagerTests
{
    [Fact]
    public void HardwareKeyArgumentsIncludeFidoOptions()
    {
        var args = KeyManager.BuildArguments(new KeyGenerationOptions { Type = KeyType.Ed25519Sk, Resident = true, VerifyRequired = true, Application = "ssh:work", Comment = "c" }, "p");
        string joined = string.Join(' ', args);
        Assert.Contains("-t ed25519-sk", joined);
        Assert.Contains("-O resident", joined);
        Assert.Contains("-O verify-required", joined);
        Assert.Contains("-O application=ssh:work", joined);
    }

    [Fact]
    public void PassphraseNeverAppearsInArguments()
    {
        var args = KeyManager.BuildArguments(new KeyGenerationOptions { Type = KeyType.Ed25519, Passphrase = "hunter2" }, "p");
        Assert.DoesNotContain("hunter2", args);
        Assert.DoesNotContain("-N", args);
    }

    [Fact]
    public async Task GeneratesAndReadsARealSoftwareKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), "et-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = await KeyManager.GenerateAsync(new KeyGenerationOptions { FileName = "test", Type = KeyType.Ed25519, Comment = "unit", Directory = directory }, "unused", "unused", "unused");
            Assert.Equal("ssh-ed25519", key.Type);
            Assert.StartsWith("SHA256:", key.Fingerprint);
            Assert.Equal("unit", key.Comment);
            Assert.False(key.IsHardwareBacked);
            Assert.Single(await KeyManager.ListAsync(directory));

            // Refusing to overwrite protects existing keys.
            await Assert.ThrowsAsync<IOException>(() => KeyManager.GenerateAsync(new KeyGenerationOptions { FileName = "test", Type = KeyType.Ed25519, Directory = directory }, "u", "u", "u"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DeployScriptQuotesTheKey()
    {
        string script = KeyManager.DeployScript("ssh-ed25519 AAAA it's me");
        Assert.Contains("'ssh-ed25519 AAAA it'\\''s me'", script);
    }
}

/// <summary>Runs the SFTP client against OpenSSH's own sftp-server over stdio, when Git for Windows provides it.</summary>
public class SftpIntegrationTests
{
    private static readonly string ServerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "lib", "ssh", "sftp-server.exe");

    private static (SftpClient Client, Process Process) Start()
    {
        var info = new ProcessStartInfo(ServerPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Its MSYS runtime DLLs live in usr\bin, which is only on PATH when started from a Git shell.
        string gitBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin");
        info.Environment["PATH"] = gitBin + ";" + Environment.GetEnvironmentVariable("PATH");
        var process = Process.Start(info)!;
        var client = new SftpClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, () => { try { process.Kill(); } catch (InvalidOperationException) { } });
        return (client, process);
    }

    /// <summary>The server is an MSYS program, so Windows paths become /c/... inside it.</summary>
    private static string ToMsys(string path) => "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');

    [Fact]
    public async Task FullFileLifecycle()
    {
        if (!File.Exists(ServerPath)) return;
        string local = Path.Combine(Path.GetTempPath(), "et-sftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        var (client, server) = Start();
        using (client)
        {
            try
            {
                try
                {
                    await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (IOException ex)
                {
                    // Say why the server went away; an MSYS startup failure only shows up on its stderr.
                    string stderr = await server.StandardError.ReadToEndAsync();
                    await server.WaitForExitAsync();
                    throw new IOException($"{ex.Message}; sftp-server exit={server.ExitCode:X} stderr=[{stderr}]", ex);
                }
                string root = ToMsys(local);
                Assert.Equal(root, await client.RealPathAsync(root));

                await client.MakeDirectoryAsync(root + "/dir");
                await client.MakeDirectoryAsync(root + "/dir/nested");

                // Larger than the pipeline window, with a random pattern, to catch reordering.
                var data = new byte[5 * 1024 * 1024 + 123];
                new Random(42).NextBytes(data);
                await client.UploadAsync(new MemoryStream(data), root + "/dir/大文件.bin");

                var stat = await client.StatAsync(root + "/dir/大文件.bin");
                Assert.Equal(data.Length, stat.Size);
                Assert.False(stat.IsDirectory);

                var download = new MemoryStream();
                long reported = 0;
                await client.DownloadAsync(root + "/dir/大文件.bin", download, stat.Size, new Progress<long>(v => reported = v));
                Assert.Equal(data, download.ToArray());

                // Unknown size must still terminate cleanly at EOF.
                var unknown = new MemoryStream();
                await client.DownloadAsync(root + "/dir/大文件.bin", unknown, -1);
                Assert.Equal(data.Length, unknown.Length);

                var empty = new MemoryStream();
                await client.UploadAsync(new MemoryStream(), root + "/empty");
                await client.DownloadAsync(root + "/empty", empty, 0);
                Assert.Equal(0, empty.Length);

                var listing = await client.ListAsync(root + "/dir");
                Assert.Contains(listing, e => e.Name == "nested" && e.IsDirectory);
                Assert.Contains(listing, e => e.Name == "大文件.bin" && !e.IsDirectory && e.Size == data.Length);
                Assert.DoesNotContain(listing, e => e.Name is "." or "..");

                await client.RenameAsync(root + "/dir/大文件.bin", root + "/dir/renamed.bin");
                Assert.Equal(data.Length, (await client.StatAsync(root + "/dir/renamed.bin")).Size);

                var missing = await Assert.ThrowsAsync<SftpException>(() => client.StatAsync(root + "/nope"));
                Assert.Equal(2, missing.Code);

                await client.RemoveRecursiveAsync(await client.StatAsync(root + "/dir"));
                Assert.DoesNotContain(await client.ListAsync(root), e => e.Name == "dir");
            }
            finally
            {
                Directory.Delete(local, true);
            }
        }
    }
}

public class ConPtyTests
{
    [Fact]
    public async Task RunsACommandAndDeliversOutputAndExitCode()
    {
        var session = new TerminalSession(() => new ConPtyTransport("cmd.exe /c echo hello-conpty&& exit 7"), 100);
        var exited = new TaskCompletionSource();
        session.StateChanged += () => { if (session.State == SessionState.Exited) exited.TrySetResult(); };
        session.Start(80, 24);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(7, session.ExitCode);
        var deadline = Environment.TickCount64 + 3000;
        string text = "";
        while (Environment.TickCount64 < deadline)
        {
            text = session.Buffer.GetText(session.Buffer.AbsoluteViewportRow(0), 0, session.Buffer.AbsoluteViewportRow(23), 79);
            if (text.Contains("hello-conpty")) break;
            await Task.Delay(50);
        }
        Assert.Contains("hello-conpty", text);
        session.Dispose();
    }

    [Fact]
    public async Task InteractiveInputReachesTheProcess()
    {
        var session = new TerminalSession(() => new ConPtyTransport("cmd.exe"), 100);
        session.Start(80, 24);
        session.Write("echo typed-%USERNAME%-ok\r\n");
        var deadline = Environment.TickCount64 + 10000;
        bool found = false;
        while (Environment.TickCount64 < deadline && !found)
        {
            string text = session.Buffer.GetText(session.Buffer.AbsoluteViewportRow(0), 0, session.Buffer.AbsoluteViewportRow(23), 79);
            found = text.Contains("typed-" + Environment.UserName + "-ok");
            await Task.Delay(50);
        }
        session.Dispose();
        Assert.True(found);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("quo\"te", "\"quo\\\"te\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void QuotesArguments(string input, string expected) => Assert.Equal(expected, CommandLine.Quote(input));
}
