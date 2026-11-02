using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Palette;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Transport;

namespace EasyTerminal.Tests;

public class AddressResolverTests
{
    /// <summary>Accepts connections and optionally sends an SSH identification line.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeServer(string? banner)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        if (banner != null) await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(banner));
                    }
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static AddressResolver Resolver(Func<IPAddress, bool>? route = null) =>
        new(_ => null, route ?? (_ => false), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(400));

    private static Device DeviceWith(params string[] addresses) =>
        new() { Host = addresses[0], AlternateAddresses = addresses.Skip(1).ToList() };

    [Theory]
    [InlineData("example.com", "example.com", null)]
    [InlineData("example.com:2222", "example.com", 2222)]
    [InlineData("192.168.1.2:22", "192.168.1.2", 22)]
    [InlineData("[fd00::1]:2200", "fd00::1", 2200)]
    [InlineData("[fd00::1]", "fd00::1", null)]
    [InlineData("fd00::1", "fd00::1", null)]
    [InlineData("  host  ", "host", null)]
    public void ParsesAddresses(string text, string host, int? port) =>
        Assert.Equal(new HostEndpoint(host, port), HostEndpoint.Parse(text));

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.31.100", true)]
    [InlineData("100.100.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void ClassifiesPrivateAddresses(string text, bool expected) =>
        Assert.Equal(expected, AddressResolver.IsPrivate(IPAddress.Parse(text)));

    [Fact]
    public async Task SkipsOffNetworkPrivateAddressInstantly()
    {
        using var server = new FakeServer("SSH-2.0-Real\r\n");
        var log = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        // 10.255.255.1 would hang until timeout if it were actually dialled.
        var chosen = await Resolver().ChooseAsync(DeviceWith("10.255.255.1", $"127.0.0.1:{server.Port}"), null, null, log.Add, default);
        stopwatch.Stop();
        Assert.Equal("127.0.0.1", chosen.Host);
        Assert.Equal(server.Port, chosen.Port);
        Assert.Contains(log, l => l.Contains("不在当前网络"));
        Assert.True(stopwatch.ElapsedMilliseconds < 800, $"Took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task PrivateAddressWithSpecificRouteIsDialled()
    {
        var seen = new List<IPAddress>();
        var resolver = Resolver(a =>
        {
            seen.Add(a);
            return true;
        });
        await resolver.ProbeAsync("192.0.2.1", 22, null, default); // TEST-NET is not private, so no route check
        Assert.Empty(seen);
        await resolver.ProbeAsync("10.255.255.1", 22, null, default);
        Assert.Single(seen);
    }

    [Fact]
    public async Task RefusedPortFallsThroughToNext()
    {
        using var server = new FakeServer("SSH-2.0-Real\r\n");
        var chosen = await Resolver().ChooseAsync(DeviceWith($"127.0.0.1:{ClosedPort()}", $"localhost:{server.Port}"), null, null, null, default);
        Assert.Equal(server.Port, chosen.Port);
    }

    [Fact]
    public async Task PrefersVerifiedSshOverSilentOpenPort()
    {
        using var silent = new FakeServer(null);
        using var real = new FakeServer("SSH-2.0-Real\r\n");
        var chosen = await Resolver().ChooseAsync(DeviceWith($"127.0.0.1:{silent.Port}", $"127.0.0.1:{real.Port}"), null, null, null, default);
        Assert.Equal(real.Port, chosen.Port);
    }

    [Fact]
    public async Task SilentPortIsTheFallbackWhenNothingIsVerified()
    {
        using var silent = new FakeServer(null);
        var chosen = await Resolver().ChooseAsync(DeviceWith($"127.0.0.1:{ClosedPort()}", $"127.0.0.1:{silent.Port}"), null, null, null, default);
        Assert.Equal(silent.Port, chosen.Port);
    }

    [Fact]
    public async Task AcceptThenHangUpCountsAsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var hangUp = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
        });
        var outcome = await Resolver().ProbeAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, null, default);
        await hangUp;
        listener.Stop();
        Assert.Equal(ProbeOutcome.Unreachable, outcome);
    }

    [Fact]
    public async Task NonSshServiceDoesNotCountAsSsh()
    {
        using var http = new FakeServer("HTTP/1.1 400 Bad Request\r\n\r\n");
        var outcome = await Resolver().ProbeAsync("127.0.0.1", http.Port, null, default);
        Assert.Equal(ProbeOutcome.OpenButNotSsh, outcome);
    }

    [Fact]
    public async Task AcceptsLinesBeforeIdentification()
    {
        using var server = new FakeServer("welcome\r\nSSH-2.0-Real\r\n");
        Assert.Equal(ProbeOutcome.SshServer, await Resolver().ProbeAsync("127.0.0.1", server.Port, null, default));
    }

    [Fact]
    public async Task AllFailedReturnsFirstSoSshReportsTheError()
    {
        var chosen = await Resolver().ChooseAsync(DeviceWith("10.255.255.1", "10.255.255.2"), null, null, null, default);
        Assert.Equal("10.255.255.1", chosen.Host);
    }

    [Fact]
    public async Task AllFailedPrefersAnAddressThatWasActuallyAttempted()
    {
        int closed = ClosedPort();
        var chosen = await Resolver().ChooseAsync(DeviceWith("10.255.255.1", $"127.0.0.1:{closed}"), null, null, null, default);
        Assert.Equal("127.0.0.1", chosen.Host);
        Assert.Equal(closed, chosen.Port);
    }

    [Fact]
    public async Task SingleAddressIsNeverProbed()
    {
        var resolver = Resolver(_ => throw new InvalidOperationException("must not probe"));
        var chosen = await resolver.ChooseAsync(DeviceWith("10.255.255.1"), null, null, null, default);
        Assert.Equal("10.255.255.1", chosen.Host);
    }

    [Fact]
    public async Task ConnectionPortOverrideIsUsedForProbing()
    {
        using var server = new FakeServer("SSH-2.0-Real\r\n");
        var chosen = await Resolver().ChooseAsync(DeviceWith("10.255.255.1", "127.0.0.1:1"), server.Port, null, null, default);
        Assert.Equal("127.0.0.1", chosen.Host);
    }

    [Fact]
    public async Task ProbesThroughProxyWithoutRouteCheck()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string? target = null;
        var proxy = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting);
            await stream.WriteAsync(new byte[] { 5, 0 });
            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            var name = new byte[head[4] + 2];
            await stream.ReadExactlyAsync(name);
            target = Encoding.ASCII.GetString(name, 0, head[4]);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await stream.WriteAsync("SSH-2.0-Behind\r\n"u8.ToArray());
            await Task.Delay(200);
        });

        var profile = new NetworkProfile { ProxyKind = ProxyKind.Socks5, ProxyHost = "127.0.0.1", ProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port };
        var chosen = await Resolver(_ => throw new InvalidOperationException("no route check via proxy"))
            .ChooseAsync(DeviceWith("192.168.31.100", "example.invalid"), null, profile, null, default);
        await proxy;
        listener.Stop();
        Assert.Equal("192.168.31.100", target);
        Assert.Equal("192.168.31.100", chosen.Host);
    }

    [Fact]
    public void LoopbackHasASpecificRoute() => Assert.True(RouteTable.HasSpecificRoute(IPAddress.Loopback));

    [Fact]
    public void ConfigUsesChosenAddressAndOneHostKeyIdentity()
    {
        var device = new Device { Name = "n", Host = "192.168.31.100", User = "me", AlternateAddresses = { "example.com:2200" } };
        var overrides = new Dictionary<Guid, HostEndpoint> { [device.Id] = new("example.com", 2200) };
        var plan = new SshConfigBuilder(_ => null, _ => null, "h.exe", null).Build(device, null, overrides);
        Assert.Contains("HostName example.com\n", plan.ConfigText);
        Assert.Contains("Port 2200\n", plan.ConfigText);
        // Every address is the same machine, so one known_hosts identity covers them all.
        Assert.Contains("HostKeyAlias 192.168.31.100\n", plan.ConfigText);

        var single = new Device { Name = "s", Host = "a.example", User = "me" };
        Assert.DoesNotContain("HostKeyAlias", new SshConfigBuilder(_ => null, _ => null, "h.exe", null).Build(single, null).ConfigText);
    }

    [Fact]
    public void ConnectionPortBeatsAddressPort()
    {
        var device = new Device { Name = "n", Host = "host:2200" };
        var plan = new SshConfigBuilder(_ => null, _ => null, "h.exe", null).Build(device, new Connection { Port = 2222 });
        Assert.Contains("Port 2222\n", plan.ConfigText);
        Assert.Contains("HostName host\n", plan.ConfigText);
    }
}

public class DeferredTransportTests
{
    private sealed class FakeTransport : ITerminalTransport
    {
        public List<string> Written { get; } = new();
        public (int Cols, int Rows) Size { get; private set; }
        public event Action<ReadOnlyMemory<byte>>? DataReceived;
        public event Action<int?>? Exited;
        public int? ProcessId => 42;
        public void Start(int cols, int rows) => Size = (cols, rows);
        public void Write(ReadOnlySpan<byte> data) => Written.Add(Encoding.ASCII.GetString(data));
        public void Resize(int cols, int rows) => Size = (cols, rows);
        public void Dispose() { }
        public void Emit(string text) => DataReceived?.Invoke(Encoding.ASCII.GetBytes(text));
        public void Finish(int code) => Exited?.Invoke(code);
    }

    [Fact]
    public async Task BuffersInputAndUsesLatestSizeUntilReady()
    {
        var gate = new TaskCompletionSource<ITerminalTransport>();
        var fake = new FakeTransport();
        using var transport = new DeferredTransport((_, _) => gate.Task);
        var output = new StringBuilder();
        int? exit = null;
        transport.DataReceived += d => output.Append(Encoding.ASCII.GetString(d.Span));
        transport.Exited += c => exit = c;
        transport.Start(80, 24);
        transport.Write("early"u8);
        transport.Resize(100, 30);
        gate.SetResult(fake);

        var deadline = Environment.TickCount64 + 3000;
        while (fake.Written.Count == 0 && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.Equal("early", fake.Written[0]);
        Assert.Equal((100, 30), fake.Size);
        Assert.Equal(42, transport.ProcessId);

        transport.Write("late"u8);
        Assert.Equal("late", fake.Written[1]);
        fake.Emit("hello");
        Assert.Equal("hello", output.ToString());
        fake.Finish(3);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task ReportsFactoryFailureAndProgress()
    {
        using var transport = new DeferredTransport((log, _) =>
        {
            log("trying");
            throw new IOException("boom");
        });
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int?>();
        transport.DataReceived += d => { lock (output) output.Append(Encoding.UTF8.GetString(d.Span)); };
        transport.Exited += c => exited.TrySetResult(c);
        transport.Start(80, 24);
        Assert.Equal(1, await exited.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("trying", output.ToString());
        Assert.Contains("boom", output.ToString());
    }
}

public class FuzzyMatcherTests
{
    [Fact]
    public void ExactBeatsSubsequenceAndPrefixBeatsMiddle()
    {
        Assert.True(FuzzyMatcher.Score("ssh", "SSH 连接") > FuzzyMatcher.Score("ssh", "open ssh"));
        Assert.True(FuzzyMatcher.Score("ssh", "open ssh") > FuzzyMatcher.Score("ssh", "shared shell host"));
    }

    [Fact]
    public void RequiresEveryTerm()
    {
        Assert.NotNull(FuzzyMatcher.Score("pi sftp", "ResberryPi SFTP"));
        Assert.Null(FuzzyMatcher.Score("pi mosh", "ResberryPi SFTP"));
    }

    [Fact]
    public void MatchesChineseAndSubsequences()
    {
        Assert.NotNull(FuzzyMatcher.Score("设备", "打开设备页"));
        Assert.NotNull(FuzzyMatcher.Score("rpi", "ResberryPi"));
        Assert.Null(FuzzyMatcher.Score("xyz", "ResberryPi"));
    }

    [Fact]
    public void EmptyQueryMatchesEverything() => Assert.Equal(0, FuzzyMatcher.Score("  ", "anything"));
}

public class RemoteDockerTests
{
    [Fact]
    public void ParsesContainersFromProbe()
    {
        string output = "@@ENV\n@@DOCKER\nWARNING: x\n{\"ID\":\"abc\",\"Names\":\"web\",\"Image\":\"nginx\",\"State\":\"running\"}\n{\"ID\":\"def\",\"Names\":\"db\",\"Image\":\"pg\",\"State\":\"exited\"}\n@@END\n";
        var result = RemoteProbe.Parse(output);
        Assert.True(result.HasDocker);
        Assert.Null(result.DockerError);
        Assert.Equal(2, result.Containers.Count);
        Assert.Equal("web", result.Containers[0].Name);
        Assert.True(result.Containers[0].IsRunning);
        Assert.False(result.Containers[1].IsRunning);
    }

    [Fact]
    public void ReportsDockerPermissionProblem()
    {
        var result = RemoteProbe.Parse("@@DOCKER\npermission denied while trying to connect to the Docker daemon socket\n@@END\n");
        Assert.True(result.HasDocker);
        Assert.Empty(result.Containers);
        Assert.Contains("权限", result.DockerError);
    }

    [Fact]
    public void AbsentDockerIsNotAnError()
    {
        var result = RemoteProbe.Parse("@@ENV\n@@END\n");
        Assert.False(result.HasDocker);
        Assert.Null(result.DockerError);
    }

    [Fact]
    public void CommandsQuoteTheContainerId()
    {
        Assert.Contains("docker exec -it", RemoteProbe.DockerExecCommand("a'b"));
        Assert.Contains("'\\''", RemoteProbe.DockerExecCommand("a'b"));
        Assert.Equal("docker stop 'abc'", RemoteProbe.DockerActionCommand("stop", "abc"));
    }

    [Fact]
    public async Task DockerExecCommandParsesUnderARealShell()
    {
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        // -n parses without running: the nested quoting has to survive both shells.
        var outer = await ProcessRunner.RunAsync(bash, new[] { "-n", "-c", RemoteProbe.DockerExecCommand("a'b") }, TimeSpan.FromSeconds(10));
        Assert.Equal(0, outer.ExitCode);
    }
}
