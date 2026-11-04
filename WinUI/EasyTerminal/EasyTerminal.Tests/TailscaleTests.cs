using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Tailscale;

namespace EasyTerminal.Tests;

public class TailscaleTests
{
    private const string StatusJson = """
        {
          "BackendState": "Running",
          "AuthURL": "",
          "Self": { "HostName": "laptop", "DNSName": "laptop.tail1234.ts.net.", "OS": "windows", "TailscaleIPs": ["100.64.0.1", "fd7a::1"] },
          "CurrentTailnet": { "Name": "me@example.com", "MagicDNSSuffix": "tail1234.ts.net" },
          "Health": [],
          "Peer": {
            "nodekey:a": { "HostName": "zeta", "DNSName": "zeta.tail1234.ts.net.", "OS": "linux", "Online": false, "TailscaleIPs": ["100.64.0.3"] },
            "nodekey:b": { "HostName": "alpha", "DNSName": "", "OS": "linux", "Online": true, "TailscaleIPs": ["100.64.0.2"] }
          }
        }
        """;

    [Fact]
    public void StatusListsOnlinePeersFirstAndPrefersMagicDns()
    {
        using var json = JsonDocument.Parse(StatusJson);
        var status = TailscaleNode.ParseStatus(json.RootElement);

        Assert.True(status.IsRunning);
        Assert.False(status.NeedsLogin);
        Assert.Equal("me@example.com", status.Tailnet);
        Assert.Equal("laptop.tail1234.ts.net", status.Self!.BestAddress);
        Assert.Equal(new[] { "alpha", "zeta" }, status.Peers.Select(p => p.HostName));
        Assert.Equal("100.64.0.2", status.Peers[0].BestAddress);
        Assert.Equal("zeta.tail1234.ts.net", status.Peers[1].BestAddress);
    }

    [Fact]
    public void NeedsLoginCarriesTheSignInPage()
    {
        using var json = JsonDocument.Parse("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/x","Self":null,"Peer":null}""");
        var status = TailscaleNode.ParseStatus(json.RootElement);
        Assert.True(status.NeedsLogin);
        Assert.Equal("https://login.tailscale.com/a/x", status.AuthUrl);
        Assert.Empty(status.Peers);
    }

    [Fact]
    public void TailscaleIsFoundThroughJumpDevices()
    {
        var hop = new Device { Name = "hop", Host = "hop", Network = new NetworkProfile { ProxyKind = ProxyKind.Tailscale } };
        var direct = new NetworkProfile();
        var viaHop = new NetworkProfile { JumpDeviceIds = { hop.Id } };
        Device? Find(Guid id) => id == hop.Id ? hop : null;

        Assert.False(ProxyRoute.UsesTailscale(direct, Find));
        Assert.True(ProxyRoute.UsesTailscale(viaHop, Find));
        Assert.True(ProxyRoute.UsesTailscale(new NetworkProfile { ProxyKind = ProxyKind.Tailscale }, Find));
    }

    [Fact]
    public void ResolvingTailscaleBeforeTheNodeStartsSaysWhatToDo()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProxyRoute.Resolve(new NetworkProfile { ProxyKind = ProxyKind.Tailscale }, _ => null));
        Assert.Contains("Tailscale", error.Message);
    }

    [Fact]
    public void OrdinaryProxiesResolveToTheirOwnSettings()
    {
        var route = ProxyRoute.Resolve(new NetworkProfile { ProxyKind = ProxyKind.Http, ProxyHost = "proxy", ProxyPort = 8080, ProxyUser = "u", ProxySecretId = "s" }, id => id == "s" ? "pw" : null);
        Assert.Equal(new ProxyRoute("http", "proxy", 8080, "u", "pw"), route);
    }

    /// <summary>Starts the real tsnet helper. Opt-in with EASYTERMINAL_TS_E2E=1, as it contacts Tailscale's control server.</summary>
    [Fact]
    public async Task RealNodeStartsAndOffersASignInPage()
    {
        if (Environment.GetEnvironmentVariable("EASYTERMINAL_TS_E2E") != "1") return;
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "EasyTerminal.slnx"))) directory = Path.GetDirectoryName(directory);
        string helper = Path.Combine(directory!, "EasyTerminal.Tailscale", "bin", "amd64", TailscaleNode.HelperFileName);
        Assert.True(File.Exists(helper), "build the app first so the helper exists");

        string state = Path.Combine(Path.GetTempPath(), "et-ts-" + Guid.NewGuid().ToString("N"));
        using var node = new TailscaleNode { OptionsProvider = () => new TailscaleOptions(helper, state, "et-e2e", "", null, true) };
        try
        {
            await node.StartAsync();
            Assert.True(node.IsStarted);
            Assert.Equal("127.0.0.1", node.Proxy.Host);
            string url = await node.LoginInteractiveAsync();
            Assert.StartsWith("https://login.tailscale.com/", url);
            var status = await node.GetStatusAsync();
            Assert.True(status.NeedsLogin);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => node.EnsureRunningAsync(null, CancellationToken.None));
            Assert.Contains("登录", error.Message);
        }
        finally
        {
            node.Stop();
            Assert.False(node.IsStarted);
            try { Directory.Delete(state, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ForwarderCarriesBytesThroughSocks5()
    {
        // A SOCKS5 proxy that checks the credentials tsnet would require, then echoes upper-cased bytes.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? requested = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var greeting = new byte[4];
            await stream.ReadExactlyAsync(greeting);
            await stream.WriteAsync(new byte[] { 5, 2 });
            var authHead = new byte[2];
            await stream.ReadExactlyAsync(authHead);
            var user = new byte[authHead[1]];
            await stream.ReadExactlyAsync(user);
            var passLength = new byte[1];
            await stream.ReadExactlyAsync(passLength);
            var pass = new byte[passLength[0]];
            await stream.ReadExactlyAsync(pass);
            bool ok = Encoding.ASCII.GetString(user) == "tsnet" && Encoding.ASCII.GetString(pass) == "secret";
            await stream.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
            if (!ok) return;
            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            var host = new byte[head[4] + 2];
            await stream.ReadExactlyAsync(host);
            requested = Encoding.ASCII.GetString(host, 0, head[4]) + ":" + (host[^2] << 8 | host[^1]);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            var buffer = new byte[5];
            await stream.ReadExactlyAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(buffer).ToUpperInvariant()));
        });

        using var forwarder = new ProxyForwarder(new ProxyRoute("socks5", "127.0.0.1", port, "tsnet", "secret"), "desktop.tail1234.ts.net", 3389);
        using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, forwarder.LocalPort);
        var localStream = local.GetStream();
        await localStream.WriteAsync("hello"u8.ToArray());
        var reply = new byte[5];
        await localStream.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("HELLO", Encoding.ASCII.GetString(reply));
        Assert.Equal("desktop.tail1234.ts.net:3389", requested);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }
}
