using System.Net;
using System.Text;
using EasyTerminal.Core.Integrations;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Monitoring;
using EasyTerminal.Core.Rdp;
using EasyTerminal.Core.Snippets;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Transport;

namespace EasyTerminal.Tests;

public class ProbeFeatureTests
{
    private const string Sample =
        "@@ENV\nmosh\n@@SYS\nhost=pve1\nkernel=Linux 6.8.12-4-pve\narch=x86_64\nos=Debian GNU/Linux 12 (bookworm)\ncpus=16\nmemkb=65536000\nmemfreekb=32768000\n" +
        "load=0.50 1.25 2.00\nuptime=86400\nvirt=none\npve=pve-manager/8.3.0/abc\n@@ENV\ntmux\n@@TMUX\n2|1|1700000000|work\n" +
        "@@TMUXWIN\n0\t1\t1\tvim\twork\teditor\n1\t3\t0\tbash\twork\tlogs|tail\n" +
        "@@DOCKERSUDO\n{\"ID\":\"c1\",\"Names\":\"web\",\"Image\":\"nginx\",\"State\":\"running\"}\n" +
        "@@PVE\nnode=pve1\nsudo=sudo -n\nuid=1000\n" +
        "@@PCT\nVMID       Status     Lock         Name\n101        running                 dns\n102        stopped    backup       build\n" +
        "@@QM\n      VMID NAME                 STATUS     MEM(MB)    BOOTDISK(GB) PID\n       200 win11                running    8192              64.00 1234\n" +
        "@@GEO\n{\n \"ip\": \"203.0.113.5\",\n \"city\": \"Tokyo\",\n \"region\": \"Tokyo\",\n \"country\": \"JP\",\n \"org\": \"AS2516 KDDI\"\n}\n@@END\n";

    [Fact]
    public void ParsesSystemFactsAndLocation()
    {
        var facts = RemoteProbe.Parse(Sample).Facts!;
        Assert.Equal("pve1", facts.Hostname);
        Assert.Equal("Debian GNU/Linux 12 (bookworm)", facts.Os);
        Assert.Equal(16, facts.CpuCount);
        Assert.Equal(65536000, facts.MemoryTotalKb);
        Assert.Equal(1.25, facts.Load5);
        Assert.Equal(86400, facts.UptimeSeconds);
        Assert.Equal("", facts.Virtualization);
        Assert.Equal("JP", facts.CountryCode);
        Assert.Equal("Tokyo", facts.City);
        Assert.Equal("AS2516 KDDI", facts.Network);
        Assert.True(facts.HasTmux);
        Assert.True(facts.HasDocker);
        Assert.True(facts.HasProxmox);
        Assert.False(RemoteProbe.Parse("@@ENV\n@@SYS\nhost=a\n@@END\n").Facts!.HasDocker);
    }

    [Fact]
    public void ParsesTmuxWindowsWithSeparatorsInNames()
    {
        var result = RemoteProbe.Parse(Sample);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(new TmuxWindow("work", 1, "logs|tail", 3, false, "bash"), result.Windows[1]);
    }

    [Fact]
    public void ParsesProxmoxGuestsWithBlankLockColumn()
    {
        var result = RemoteProbe.Parse(Sample);
        Assert.True(result.HasProxmox);
        Assert.True(result.ProxmoxNeedsSudo);
        Assert.Equal("pve1", result.ProxmoxNode);
        Assert.Equal(3, result.Guests.Count);
        Assert.Equal("dns", result.Guests[0].Name);
        Assert.True(result.Guests[0].IsRunning);
        Assert.Equal("build", result.Guests[1].Name);
        Assert.False(result.Guests[1].IsRunning);
        Assert.Equal(PveGuestKind.Qemu, result.Guests[2].Kind);
        Assert.Equal("win11", result.Guests[2].Name);
        Assert.Equal("VM · 8192 MB", result.Guests[2].Detail);
    }

    [Fact]
    public void ProxmoxWithoutRootExplainsWhy()
    {
        var result = RemoteProbe.Parse("@@PVE\nnode=n\nsudo=\nuid=1000\n@@PCT\nonly root can run this command\n@@QM\nonly root can run this command\n@@END\n");
        Assert.True(result.HasProxmox);
        Assert.Empty(result.Guests);
        Assert.NotNull(result.ProxmoxError);
    }

    [Fact]
    public void DockerSectionViaSudoIsMarked()
    {
        var result = RemoteProbe.Parse(Sample);
        Assert.True(result.DockerNeedsSudo);
        Assert.Single(result.Containers);
    }

    [Fact]
    public void GarbageGeoAnswerLeavesLocationEmpty()
    {
        var facts = RemoteProbe.Parse("@@SYS\nhost=a\n@@GEO\n<html>portal</html>\n@@END\n").Facts!;
        Assert.Equal("", facts.CountryCode);
    }

    [Fact]
    public async Task ScriptsParseAndRunUnderARealShell()
    {
        string bash = FindBash();
        if (bash.Length == 0) return;
        foreach (var script in new[] { RemoteProbe.BuildScript(true), HostMonitor.Script })
        {
            var check = await ProcessRunner.RunAsync(bash, new[] { "-n", "-c", script }, TimeSpan.FromSeconds(10));
            Assert.True(check.ExitCode == 0, check.Error);
        }
        var run = await ProcessRunner.RunAsync(bash, new[] { "-c", RemoteProbe.Script }, TimeSpan.FromSeconds(30));
        var parsed = RemoteProbe.Parse(run.Output);
        Assert.NotNull(parsed.Facts);
    }

    private static string FindBash()
    {
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(bash) ? bash : "";
    }

    [Fact]
    public void TmuxCommandsMatchExactSessionAndQuote()
    {
        Assert.Equal("tmux rename-session -t '=a'\\''b' 'new'", TmuxCommands.RenameSession("a'b", "new"));
        Assert.Contains("-r -t", TmuxCommands.Attach("s", readOnly: true).Replace("'\\''", "'"));
        Assert.Contains("=s:2", TmuxCommands.AttachWindow("s", 2));
        Assert.Equal("tmux kill-window -t '=s:2'", TmuxCommands.KillWindow("s", 2));
        Assert.Equal("tmux new-window -t '=s:' -n 'w'", TmuxCommands.NewWindow("s", "w"));
        Assert.False(TmuxCommands.IsValidSessionName("a.b"));
        Assert.False(TmuxCommands.IsValidSessionName("a:b"));
        Assert.True(TmuxCommands.IsValidSessionName("中文 名"));
    }

    [Fact]
    public void ProxmoxCommandsPickTheRightTool()
    {
        var lxc = new PveGuest(PveGuestKind.Lxc, 101, "dns", "running", "LXC");
        var vm = new PveGuest(PveGuestKind.Qemu, 200, "win", "stopped", "VM");
        Assert.Contains("pct enter 101", ProxmoxCommands.Enter(lxc, false));
        Assert.Contains("qm terminal 200", ProxmoxCommands.Enter(vm, false));
        Assert.Contains("sudo pct enter 101", ProxmoxCommands.Enter(lxc, true));
        Assert.EndsWith("sudo -n qm start 200", ProxmoxCommands.Action(vm, "start", true));
        Assert.Equal("https://10.0.0.1:8006/?console=kvm&novnc=1&vmid=200&vmname=win&node=pve%201&resize=scale", ProxmoxCommands.WebConsoleUrl("10.0.0.1", "pve 1", vm));
    }

    [Fact]
    public void AgentAddressPrefersRoutableIpv4()
    {
        const string json = "[{\"name\":\"lo\",\"ip-addresses\":[{\"ip-address\":\"127.0.0.1\",\"ip-address-type\":\"ipv4\"}]}," +
                            "{\"name\":\"eth0\",\"ip-addresses\":[{\"ip-address\":\"fe80::1\",\"ip-address-type\":\"ipv6\"},{\"ip-address\":\"10.0.0.9\",\"ip-address-type\":\"ipv4\"}]}]";
        Assert.Equal("10.0.0.9", ProxmoxCommands.ParseAgentAddress(json));
        Assert.Null(ProxmoxCommands.ParseAgentAddress("not json"));
        Assert.Null(ProxmoxCommands.ParseAgentAddress("[]"));
    }
}

public class MonitorParserTests
{
    private static MonitorSample Feed(MonitorParser parser, params string[] lines)
    {
        MonitorSample? sample = null;
        foreach (var line in lines) sample = parser.Feed(line) ?? sample;
        return sample!;
    }

    [Fact]
    public void FirstSampleHasNoRatesAndSecondComputesThem()
    {
        var parser = new MonitorParser();
        var first = Feed(parser, "T 100.00", "C 100 0 100 800 0 0 0 0 0 0", "L 0.5 0.6 0.7 2/300", "M 1000 400 200 100", "N 1000 2000", "I 10 20", "D 2000 500 /", "P 12.5 3.0 nginx", "E");
        Assert.Null(first.CpuPercent);
        Assert.Null(first.NetworkRxBytesPerSecond);
        Assert.Equal(60, first.MemoryPercent);
        Assert.Equal("2/300", first.Tasks);
        Assert.Equal(25, Assert.Single(first.Disks).Percent);
        Assert.Equal("nginx", Assert.Single(first.Processes).Name);

        var second = Feed(parser, "T 102.00", "C 150 0 150 900 0 0 0 0 0 0", "L 1 1 1", "M 1000 500 200 100", "N 3000 2500", "I 14 22", "E");
        Assert.Equal(50, second.CpuPercent);
        Assert.Equal(1000, second.NetworkRxBytesPerSecond);
        Assert.Equal(250, second.NetworkTxBytesPerSecond);
        Assert.Equal(1024, second.DiskReadBytesPerSecond);
        // df only runs now and then, and processes are per reading.
        Assert.Single(second.Disks);
        Assert.Empty(second.Processes);
    }

    [Fact]
    public void CounterResetIsNotReportedAsTraffic()
    {
        var parser = new MonitorParser();
        Feed(parser, "T 10", "N 5000 5000", "E");
        var sample = Feed(parser, "T 12", "N 100 100", "E");
        Assert.Null(sample.NetworkRxBytesPerSecond);
    }

    [Fact]
    public void HostWithoutProcFilesStillGivesLoad()
    {
        var sample = Feed(new MonitorParser(), "L 1.5 1.0 0.5", "D 100 50 /", "E");
        Assert.Null(sample.CpuPercent);
        Assert.Null(sample.MemoryPercent);
        Assert.Equal(1.5, sample.Load1);
    }

    [Fact]
    public void IowaitCountsAsIdleAndGuestIsNotCountedTwice()
    {
        var before = new long[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var after = new long[] { 50, 0, 0, 25, 25, 0, 0, 0, 50, 0 };
        Assert.Equal(50, MonitorParser.CpuPercent(before, after));
    }
}

public class SystemAndToolTests
{
    [Theory]
    [InlineData("Ubuntu 22.04.5 LTS", "Linux 6.8.0-1060-oracle", "", "ubuntu")]
    [InlineData("Debian GNU/Linux 12 (bookworm)", "Linux 6.8.12-4-pve", "pve-manager/8.3.0", "proxmox")]
    [InlineData("Pop!_OS 22.04 LTS", "Linux 6.9", "", "popos")]
    [InlineData("Linux Mint 21.3", "Linux 6.5", "", "linuxmint")]
    [InlineData("Raspbian GNU/Linux 11 (bullseye)", "Linux 6.1", "", "raspberrypi")]
    [InlineData("Rocky Linux 9.4 (Blue Onyx)", "Linux 5.14", "", "rockylinux")]
    [InlineData("Alpine Linux v3.20", "Linux 6.6", "", "alpinelinux")]
    [InlineData("macOS 14.5", "Darwin 23.5.0", "", "apple")]
    [InlineData("", "FreeBSD 14.1-RELEASE", "", "freebsd")]
    [InlineData("Some Custom Distro", "Linux 6.1", "", "linux")]
    [InlineData("", "", "", null)]
    public void PicksALogoFromTheScan(string os, string kernel, string pve, string? expected)
    {
        Assert.Equal(expected, OsIdentity.LogoFor(new DeviceFacts { Os = os, Kernel = kernel, ProxmoxVersion = pve }));
    }

    [Fact]
    public void NoScanMeansNoLogo() => Assert.Null(OsIdentity.LogoFor(null));

    [Fact]
    public void ToolsPackageManagerAndRootComeFromTheProbe()
    {
        var facts = RemoteProbe.Parse("@@SYS\nhost=a\nuid=0\n@@TOOLS\nhtop\nmtr\nnot-a-known-tool\nhtop\npm=apt-get\n@@END\n").Facts!;
        Assert.Equal(new[] { "htop", "mtr" }, facts.Tools);
        Assert.Equal("apt-get", facts.PackageManager);
        Assert.True(facts.IsRoot);
        Assert.False(RemoteProbe.Parse("@@SYS\nuid=1000\n@@END\n").Facts!.IsRoot);
        Assert.Equal("Android (Termux)", RemoteProbe.Parse("@@ENV\ntermux\n@@SYS\nkernel=Linux 5.4\n@@END\n").Facts!.Os);
    }

    [Fact]
    public void LaunchUsesSudoOnlyWhenNeededAndKeepsReportsReadable()
    {
        var iftop = ToolCatalog.Find("iftop")!;
        Assert.Contains("exec sudo iftop", ToolCatalog.LaunchCommand(iftop, "", isRoot: false));
        Assert.DoesNotContain("sudo", ToolCatalog.LaunchCommand(iftop, "", isRoot: true));
        var mtr = ToolCatalog.Find("mtr")!;
        Assert.True(mtr.NeedsTarget);
        Assert.Contains("mtr '\\''1.1.1.1'\\''", ToolCatalog.LaunchCommand(mtr, "1.1.1.1", true));
        Assert.Contains("vnstat; exec", ToolCatalog.LaunchCommand(ToolCatalog.Find("vnstat")!, "", true));
    }

    [Fact]
    public void InstallMatchesThePackageManager()
    {
        var htop = ToolCatalog.Find("htop")!;
        Assert.Contains("apt-get install -y", ToolCatalog.InstallCommand(htop, "apt-get", isRoot: true));
        Assert.Contains("sudo sh -c", ToolCatalog.InstallCommand(htop, "dnf", isRoot: false));
        Assert.Contains("apk add", ToolCatalog.InstallCommand(htop, "apk", isRoot: true));
        Assert.DoesNotContain("sudo", ToolCatalog.InstallCommand(htop, "pkg", isRoot: false));
        Assert.Null(ToolCatalog.InstallCommand(htop, "unknown", true));
        Assert.Null(ToolCatalog.InstallCommand(ToolCatalog.Find("ctop")!, "apt-get", true));
    }

    [Fact]
    public async Task ToolCommandsParseUnderARealShell()
    {
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var command in new[] { ToolCatalog.LaunchCommand(tool, "a'b", false), ToolCatalog.InstallCommand(tool, "apt-get", false) })
            {
                if (command == null) continue;
                var check = await ProcessRunner.RunAsync(bash, new[] { "-n", "-c", command }, TimeSpan.FromSeconds(10));
                Assert.True(check.ExitCode == 0, tool.Name + ": " + check.Error);
            }
        }
    }
}

public class NetworkDiagnosticsTests
{
    [Fact]
    public void ParsesPingCurlPortsAndInterfaces()
    {
        const string output =
            "@@PING\n1.1.1.1|0% packet loss|= 1.234/2.500\n8.8.8.8|100% packet loss|\n223.5.5.5||\n" +
            "@@HTTP\nhttps://github.com|0.012 0.045 0.210 200\nhttps://www.google.com|0.000 0.000 0.000 000\n" +
            "@@PORTS\ntcp   LISTEN 0      4096   0.0.0.0:22        0.0.0.0:*    users:((\"sshd\",pid=1,fd=3))\n" +
            "udp   UNCONN 0      0      [::]:53           [::]:*\n" +
            "tcp        0      0 127.0.0.1:5432          0.0.0.0:*               LISTEN\n" +
            "Active Internet connections (only servers)\n" +
            "@@IF\nlo  UNKNOWN 127.0.0.1/8\neth0 UP 10.0.0.5/24\n@@END\n";
        var report = NetworkDiagnostics.Parse(output);

        Assert.Equal(0, report.Pings[0].LossPercent);
        Assert.Equal(2.5, report.Pings[0].AverageMs);
        Assert.Equal("Cloudflare DNS", report.Pings[0].Label);
        Assert.Equal(100, report.Pings[1].LossPercent);
        Assert.Null(report.Pings[2].LossPercent);

        Assert.True(report.HasCurl);
        Assert.Equal(200, report.Http[0].Status);
        Assert.Equal(45, report.Http[0].ConnectMs!.Value, 3);
        Assert.Null(report.Http[1].Status);

        Assert.Equal(new[] { 22, 53, 5432 }, report.Ports.Select(p => p.Port));
        Assert.Equal("sshd", report.Ports[0].Process);
        Assert.Equal("UDP", report.Ports[1].Protocol);
        Assert.Equal("::", report.Ports[1].Address);
        Assert.Equal(2, report.Interfaces.Count);
    }

    [Fact]
    public async Task ScriptRunsUnderARealShell()
    {
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        var check = await ProcessRunner.RunAsync(bash, new[] { "-n", "-c", NetworkDiagnostics.Script }, TimeSpan.FromSeconds(10));
        Assert.True(check.ExitCode == 0, check.Error);
    }

    [Fact]
    public async Task MeasuresConnectTimeAndReportsRefusalAsNoAnswer()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.NotNull(await NetworkDiagnostics.MeasureConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(3)));
        listener.Stop();
        Assert.Null(await NetworkDiagnostics.MeasureConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(3)));
    }
}

public class LiveTransportTests
{
    [Fact]
    public async Task MonitorScriptProducesSamplesTheParserUnderstands()
    {
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        var info = new System.Diagnostics.ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(HostMonitor.Script);
        using var process = System.Diagnostics.Process.Start(info)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var parser = new MonitorParser();
            var samples = new List<MonitorSample>();
            while (samples.Count < 2 && await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                if (parser.Feed(line.TrimEnd('\r')) is { } sample) samples.Add(sample);

            Assert.Equal(2, samples.Count);
            Assert.True(samples[1].MemoryTotalKb > 0);
            Assert.NotNull(samples[1].CpuPercent);
            Assert.InRange(samples[1].CpuPercent!.Value, 0, 100);
        }
        finally
        {
            process.Kill(true);
        }
    }

    [Fact]
    public async Task TelnetNegotiatesAndCarriesDataBothWays()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var transport = new TelnetTransport("127.0.0.1", port);
        var text = new StringBuilder();
        var greeted = new TaskCompletionSource();
        transport.DataReceived += data =>
        {
            lock (text) text.Append(Encoding.ASCII.GetString(data.Span));
            greeted.TrySetResult();
        };
        transport.Start(100, 40);

        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        // Asks for the window size, then sends the greeting.
        await stream.WriteAsync(new byte[] { 255, 253, 31 });
        await stream.WriteAsync(Encoding.ASCII.GetBytes("login: "));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<byte>();
        var buffer = new byte[256];
        // WILL NAWS (3 bytes) plus the size message (9 bytes).
        while (received.Count < 12) received.AddRange(buffer[..await stream.ReadAsync(buffer, timeout.Token)]);
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 100, 0, 40, 255, 240 }, received.Take(12));

        await greeted.Task.WaitAsync(timeout.Token);
        lock (text) Assert.Equal("login: ", text.ToString());

        transport.Write(Encoding.ASCII.GetBytes("root\r"));
        int read = await stream.ReadAsync(buffer, timeout.Token);
        Assert.Equal("root\r", Encoding.ASCII.GetString(buffer, 0, read));
        listener.Stop();
    }
}

public class GitHubClientTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task UploadSendsTitleAndKeyWithBearerToken()
    {
        var stub = new Stub(HttpStatusCode.Created, "{\"id\":7,\"title\":\"t\",\"key\":\"ssh-ed25519 AAAA\"}");
        var key = await new GitHubClient("secret", stub).AddKeyAsync("t", "ssh-ed25519 AAAA me@host\n");
        Assert.Equal(7, key.Id);
        Assert.Equal(HttpMethod.Post, stub.Last!.Method);
        Assert.Equal("https://api.github.com/user/keys", stub.Last.RequestUri!.ToString());
        Assert.Equal("secret", stub.Last.Headers.Authorization!.Parameter);
        Assert.Contains("\"key\":\"ssh-ed25519 AAAA me@host\"", stub.LastBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "无效")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}", "admin:public_key")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Validation Failed\",\"errors\":[{\"message\":\"key is already in use\"}]}", "已经绑定")]
    [InlineData(HttpStatusCode.InternalServerError, "oops", "500")]
    public async Task FailuresAreExplained(HttpStatusCode status, string body, string expected)
    {
        var error = await Assert.ThrowsAsync<IOException>(() => new GitHubClient("x", new Stub(status, body)).AddKeyAsync("t", "ssh-rsa A"));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void KeysCompareWithoutTheirComments()
    {
        Assert.True(GitHubClient.SameKey("ssh-ed25519 AAAA me@host", "ssh-ed25519 AAAA"));
        Assert.False(GitHubClient.SameKey("ssh-ed25519 AAAA", "ssh-ed25519 BBBB"));
        Assert.False(GitHubClient.SameKey("ssh-rsa AAAA", "ssh-ed25519 AAAA"));
        Assert.False(GitHubClient.SameKey("", ""));
    }
}

public class SnippetTemplateTests
{
    [Fact]
    public void FindsEachVariableOnce()
    {
        Assert.Equal(new[] { "host", "端口" }, SnippetTemplate.Variables("ping {{host}} {{ 端口 }} {{host}} ${HOME} {{}}"));
    }

    [Fact]
    public void FillsKnownAndKeepsUnknown()
    {
        Assert.Equal("a b {{z}}", SnippetTemplate.Fill("{{x}} {{ y }} {{z}}", new Dictionary<string, string> { ["x"] = "a", ["y"] = "b" }));
    }

    [Fact]
    public void ValuesAreNotReexpanded()
    {
        Assert.Equal("{{y}}", SnippetTemplate.Fill("{{x}}", new Dictionary<string, string> { ["x"] = "{{y}}", ["y"] = "no" }));
    }

    [Fact]
    public void StartupCommandEndsInLoginShell()
    {
        Assert.Equal("cd /srv\nexec \"${SHELL:-sh}\" -l", SnippetTemplate.StartupCommand("cd /srv\r\n"));
    }
}

public class PortForwardTests
{
    [Fact]
    public void BuildsSshArguments()
    {
        Assert.Equal(new[] { "-L", "127.0.0.1:8080:db:5432" }, PortForwardArguments.For(new PortForward { Kind = PortForwardKind.Local, BindPort = 8080, TargetHost = "db", TargetPort = 5432 }));
        Assert.Equal(new[] { "-R", "0.0.0.0:80:[::1]:3000" }, PortForwardArguments.For(new PortForward { Kind = PortForwardKind.Remote, BindAddress = "0.0.0.0", BindPort = 80, TargetHost = "::1", TargetPort = 3000 }));
        Assert.Equal(new[] { "-D", "127.0.0.1:1080" }, PortForwardArguments.For(new PortForward { Kind = PortForwardKind.Dynamic, BindPort = 1080 }));
    }

    [Fact]
    public void ValidatesPorts()
    {
        Assert.NotNull(PortForwardArguments.Validate(new PortForward { BindPort = 0, TargetPort = 1 }));
        Assert.NotNull(PortForwardArguments.Validate(new PortForward { BindPort = 1, TargetPort = 0 }));
        Assert.Null(PortForwardArguments.Validate(new PortForward { Kind = PortForwardKind.Dynamic, BindPort = 1 }));
    }
}

public class SshConfigImportTests
{
    private const string Config = """
        # comment
        Host *
          ServerAliveInterval 30
        Host bastion
          HostName 1.2.3.4
          User admin
          Port 2222
        Host app app-alias
          HostName app.internal
          User deploy
          IdentityFile ~/.ssh/id_app
          ProxyJump admin@bastion:2222
          ForwardAgent yes
        Host *.example.com
          User nobody
        Match host foo
          User ignored
        """;

    [Fact]
    public void ImportsConcreteHostsAndLinksJumps()
    {
        var devices = SshConfigImporter.Parse(Config, Array.Empty<Device>());
        Assert.Equal(new[] { "bastion", "app", "app-alias" }, devices.Select(d => d.Name));
        var bastion = devices[0];
        Assert.Equal(("1.2.3.4", "admin", 2222), (bastion.Host, bastion.User, bastion.Port));
        var app = devices[1];
        Assert.Equal(AuthMethod.KeyFile, app.Auth);
        Assert.EndsWith("id_app", app.KeyPath);
        Assert.True(app.ForwardAgent);
        Assert.Equal(bastion.Id, Assert.Single(app.Network.JumpDeviceIds));
    }

    [Fact]
    public void SkipsDevicesThatAlreadyExist()
    {
        var existing = new Device { Host = "1.2.3.4", User = "admin", Port = 2222 };
        var devices = SshConfigImporter.Parse(Config, new[] { existing });
        Assert.DoesNotContain(devices, d => d.Name == "bastion");
        Assert.Equal(existing.Id, devices.First(d => d.Name == "app").Network.JumpDeviceIds[0]);
    }
}

public class KnownHostsTests
{
    [Fact]
    public void ParsesPlainHashedAndMarkedEntries()
    {
        const string key = "AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
        var plain = KnownHostsFile.ParseLine($"github.com ssh-ed25519 {key}", 0)!;
        Assert.Equal("github.com", plain.Hosts);
        Assert.False(plain.IsHashed);
        Assert.Equal("SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU", plain.Fingerprint);
        Assert.True(KnownHostsFile.ParseLine($"|1|abc|def ssh-ed25519 {key}", 1)!.IsHashed);
        Assert.Equal("ssh-ed25519", KnownHostsFile.ParseLine($"@revoked host ssh-ed25519 {key}", 2)!.KeyType);
        Assert.Null(KnownHostsFile.ParseLine("# comment", 3));
    }

    [Fact]
    public void RemoveOnlyTouchesTheMatchingLineAndRefusesWhenFileChanged()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[] { "# keep", "a ssh-rsa AAAA", "b ssh-rsa BBBB" });
            var entries = KnownHostsFile.Read(path);
            Assert.Equal(2, entries.Count);
            var stale = entries[0];
            Assert.True(KnownHostsFile.Remove(entries[1], path));
            Assert.Equal(new[] { "# keep", "a ssh-rsa AAAA" }, File.ReadAllLines(path));

            File.WriteAllLines(path, new[] { "# keep", "other ssh-rsa CCCC" });
            Assert.False(KnownHostsFile.Remove(stale, path));
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class TelnetFilterTests
{
    private const byte Iac = 255, Do = 253, Dont = 254, Will = 251, Wont = 252, Sb = 250, Se = 240;

    [Fact]
    public void PassesDataAndUnescapesIac()
    {
        var (text, reply) = new TelnetTransport.TelnetFilter().Process(new byte[] { 65, Iac, Iac, 66 });
        Assert.Equal(new byte[] { 65, 255, 66 }, text);
        Assert.Empty(reply);
    }

    [Fact]
    public void AcceptsServerEchoAndRefusesUnknownOptions()
    {
        var filter = new TelnetTransport.TelnetFilter();
        Assert.Equal(new byte[] { Iac, Do, 1 }, filter.Process(new byte[] { Iac, Will, 1 }).Reply);
        Assert.Equal(new byte[] { Iac, Dont, 99 }, filter.Process(new byte[] { Iac, Will, 99 }).Reply);
        Assert.Equal(new byte[] { Iac, Wont, 99 }, filter.Process(new byte[] { Iac, Do, 99 }).Reply);
    }

    [Fact]
    public void AgreesWindowSizeAndAnswersTerminalType()
    {
        var filter = new TelnetTransport.TelnetFilter();
        Assert.Equal(new byte[] { Iac, Will, 31 }, filter.Process(new byte[] { Iac, Do, 31 }).Reply);
        Assert.True(filter.WindowSizeAgreed);
        var reply = filter.Process(new byte[] { Iac, Sb, 24, 1, Iac, Se }).Reply;
        Assert.Contains("xterm-256color", Encoding.ASCII.GetString(reply));
    }

    [Fact]
    public void CommandSplitAcrossReadsIsKept()
    {
        var filter = new TelnetTransport.TelnetFilter();
        Assert.Empty(filter.Process(new byte[] { 65, Iac }).Reply);
        var (text, reply) = filter.Process(new byte[] { Will, 1, 66 });
        Assert.Equal(new byte[] { Iac, Do, 1 }, reply);
        Assert.Equal(new byte[] { 66 }, text);
    }

    [Fact]
    public void WindowSizeEscapesIacBytes()
    {
        Assert.Equal(new byte[] { Iac, Sb, 31, 0, Iac, Iac, 0, 24, Iac, Se }, TelnetTransport.TelnetFilter.WindowSizeMessage(255, 24));
    }

    [Fact]
    public void OutgoingEscapesIac()
    {
        Assert.Equal(new byte[] { 1, Iac, Iac, 2 }, TelnetTransport.TelnetFilter.EscapeOutgoing(new byte[] { 1, Iac, 2 }));
    }
}

public class RdpFileTests
{
    [Fact]
    public void WritesAddressAndCredentialsWithoutPassword()
    {
        string text = RdpFile.Build(new RdpTarget("win.local", 3390, "me", "CORP", true, false));
        Assert.Contains("full address:s:win.local:3390\r\n", text);
        Assert.Contains("username:s:me\r\n", text);
        Assert.Contains("domain:s:CORP\r\n", text);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultPortIsOmittedAndIpv6IsBracketed()
    {
        Assert.Contains("full address:s:win\r\n", RdpFile.Build(new RdpTarget("win", 3389, "", "", true, false)));
        Assert.Contains("full address:s:[::1]:3390\r\n", RdpFile.Build(new RdpTarget("::1", 3390, "", "", true, false)));
    }

    [Fact]
    public void LineBreaksCannotInjectSettings()
    {
        string text = RdpFile.Build(new RdpTarget("h", 3389, "a\r\nshell working directory:s:x", "", false, false));
        Assert.DoesNotContain("\nshell working", text);
    }
}

public class ExportImportTests
{
    private static AppStore NewStore() => new(Path.Combine(Path.GetTempPath(), "et-test-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void OlderSshDevicesGainTheMonitorOnceAndDeletionSticks()
    {
        string directory = Path.Combine(Path.GetTempPath(), "et-test-" + Guid.NewGuid().ToString("N"));
        var first = new AppStore(directory);
        var ssh = new Device { Name = "ssh", Connections = { new Connection { Protocol = ConnectionProtocol.Ssh } } };
        var rdp = new Device { Name = "rdp", Connections = { new Connection { Protocol = ConnectionProtocol.Rdp } } };
        first.Data.Devices.AddRange(new[] { ssh, rdp });
        first.Save();

        var second = new AppStore(directory);
        second.Load();
        var migrated = second.Data.Devices[0];
        Assert.Equal(new[] { ConnectionProtocol.Ssh, ConnectionProtocol.Monitor }, migrated.Connections.Select(c => c.Protocol));
        Assert.Equal("状态", migrated.Connections[1].Name);
        Assert.Single(second.Data.Devices[1].Connections);

        migrated.Connections.RemoveAll(c => c.Protocol == ConnectionProtocol.Monitor);
        second.Save();
        var third = new AppStore(directory);
        third.Load();
        Assert.DoesNotContain(third.Data.Devices[0].Connections, c => c.Protocol == ConnectionProtocol.Monitor);
    }

    [Fact]
    public void SeparateNetworkAndToolsSessionsFoldIntoStatus()
    {
        var device = new Device
        {
            Connections =
            {
                new Connection { Name = "性能", Protocol = ConnectionProtocol.Monitor },
                new Connection { Name = "网络", Protocol = ConnectionProtocol.Network },
                new Connection { Name = "工具", Protocol = ConnectionProtocol.Tools },
                new Connection { Name = "我的监控", Protocol = ConnectionProtocol.Monitor },
            },
        };
        device.MergeStatusSessions();
        Assert.Equal(new[] { "状态", "我的监控" }, device.Connections.Select(c => c.Name));
    }

    [Fact]
    public void SoftwareSessionsShowOnlyOnceAScanFoundTheSoftware()
    {
        var device = new Device();
        var tmuxList = new Connection { Protocol = ConnectionProtocol.Tmux };
        var namedTmux = new Connection { Protocol = ConnectionProtocol.Tmux, SessionName = "work" };
        var docker = new Connection { Protocol = ConnectionProtocol.Docker };
        var ssh = new Connection { Protocol = ConnectionProtocol.Ssh };
        Assert.False(device.Shows(tmuxList));
        Assert.False(device.Shows(docker));
        Assert.True(device.Shows(namedTmux));
        Assert.True(device.Shows(ssh));

        device.Facts = RemoteProbe.Parse("@@ENV\ntmux\n@@SYS\nhost=a\n@@END\n").Facts;
        Assert.True(device.Shows(tmuxList));
        Assert.False(device.Shows(docker));
    }

    [Fact]
    public void OfferAddsOnceAndKeepsAnExistingOne()
    {
        var device = new Device { Connections = { new Connection { Name = "mine", Protocol = ConnectionProtocol.Docker } } };
        device.Offer(ConnectionProtocol.Docker, "Docker");
        device.Offer(ConnectionProtocol.Tmux, "tmux");
        Assert.Equal(new[] { "mine", "tmux" }, device.Connections.Select(c => c.Name));
        device.Connections.RemoveAll(c => c.Protocol == ConnectionProtocol.Tmux);
        device.Offer(ConnectionProtocol.Tmux, "tmux");
        Assert.DoesNotContain(device.Connections, c => c.Protocol == ConnectionProtocol.Tmux);
    }

    [Fact]
    public void ExportLeavesSecretReferencesBehind()
    {
        var store = NewStore();
        store.Data.GitHubTokenSecretId = "gh";
        store.Data.Devices.Add(new Device { Name = "d", PasswordSecretId = "p", Network = new NetworkProfile { ProxySecretId = "x" }, Connections = { new Connection { PasswordSecretId = "r" } } });
        string json = store.Export();
        foreach (var secret in new[] { "\"gh\"", "\"p\"", "\"x\"", "\"r\"" }) Assert.DoesNotContain(secret, json);
        Assert.NotNull(store.Data.Devices[0].PasswordSecretId);
    }

    [Fact]
    public void ImportAddsOnlyMissingAndNeverAutoStartsForwards()
    {
        var source = NewStore();
        var keep = new Device { Name = "source name" };
        source.Data.Devices.Add(keep);
        source.Data.Snippets.Add(new Snippet { Name = "s" });
        source.Data.PortForwards.Add(new PortForward { Name = "f", AutoStart = true });

        var target = NewStore();
        target.Data.Devices.Add(new Device { Id = keep.Id, Name = "local name" });
        Assert.Equal(2, target.Import(source.Export()));
        Assert.Equal("local name", Assert.Single(target.Data.Devices).Name);
        Assert.False(Assert.Single(target.Data.PortForwards).AutoStart);
    }
}
