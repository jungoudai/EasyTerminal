using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EasyTerminal.Core.Monitoring;

public sealed record PingResult(string Target, string Label, double? LossPercent, double? AverageMs);

public sealed record HttpResult(string Url, string Label, double? DnsMs, double? ConnectMs, double? FirstByteMs, int? Status);

public sealed record ListeningPort(string Protocol, string Address, int Port, string Process);

public sealed record NetworkReport(IReadOnlyList<PingResult> Pings, IReadOnlyList<HttpResult> Http, IReadOnlyList<ListeningPort> Ports, IReadOnlyList<string> Interfaces, bool HasCurl);

/// <summary>Checks how a server reaches the outside world, and what it listens on, with one read-only script.</summary>
public static partial class NetworkDiagnostics
{
    public static readonly IReadOnlyList<(string Target, string Label)> PingTargets = new[]
    {
        ("1.1.1.1", "Cloudflare DNS"), ("8.8.8.8", "Google DNS"), ("223.5.5.5", "阿里 DNS"), ("114.114.114.114", "114 DNS"),
    };

    public static readonly IReadOnlyList<(string Url, string Label)> HttpTargets = new[]
    {
        ("https://www.google.com", "Google"), ("https://github.com", "GitHub"), ("https://www.cloudflare.com", "Cloudflare"), ("https://www.baidu.com", "百度"),
    };

    public static string Script =>
        "export LC_ALL=C; PATH=\"$PATH:/usr/sbin:/sbin\";" +
        "echo @@PING; for h in " + string.Join(' ', PingTargets.Select(t => t.Target)) + "; do r=$(ping -c 3 -W 2 -q $h 2>&1); " +
        "echo \"$h|$(echo \"$r\" | grep -oE '[0-9.]+% packet loss' | head -n 1)|$(echo \"$r\" | grep -oE '= [0-9.]+/[0-9.]+' | head -n 1)\"; done;" +
        "if command -v curl >/dev/null 2>&1; then echo @@HTTP; for u in " + string.Join(' ', HttpTargets.Select(t => t.Url)) + "; do " +
        "echo \"$u|$(curl -o /dev/null -s -m 8 -w '%{time_namelookup} %{time_connect} %{time_starttransfer} %{http_code}' $u)\"; done; fi;" +
        "echo @@PORTS; ss -tulnpH 2>/dev/null || ss -tulnH 2>/dev/null || netstat -tuln 2>/dev/null;" +
        "echo @@IF; ip -br addr 2>/dev/null || ifconfig 2>/dev/null | grep -E '^[a-z0-9]|inet ';" +
        "echo @@END";

    [GeneratedRegex(@"([0-9.]+)% packet loss")]
    private static partial Regex LossPattern();

    [GeneratedRegex(@"= [0-9.]+/([0-9.]+)")]
    private static partial Regex AveragePattern();

    [GeneratedRegex("users:\\(\\(\"([^\"]+)\"")]
    private static partial Regex ProcessPattern();

    public static NetworkReport Parse(string output)
    {
        var pings = new List<PingResult>();
        var http = new List<HttpResult>();
        var ports = new List<ListeningPort>();
        var interfaces = new List<string>();
        bool hasCurl = false;
        string section = "";
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                section = line;
                if (line == "@@HTTP") hasCurl = true;
                continue;
            }
            if (line.Trim().Length == 0) continue;
            switch (section)
            {
                case "@@PING":
                    if (ParsePing(line) is { } ping) pings.Add(ping);
                    break;
                case "@@HTTP":
                    if (ParseHttp(line) is { } result) http.Add(result);
                    break;
                case "@@PORTS":
                    if (ParsePort(line) is { } port && !ports.Any(p => p.Protocol == port.Protocol && p.Port == port.Port && p.Address == port.Address)) ports.Add(port);
                    break;
                case "@@IF":
                    interfaces.Add(line.Trim());
                    break;
            }
        }
        return new NetworkReport(pings, http, ports.OrderBy(p => p.Port).ThenBy(p => p.Protocol).ToList(), interfaces, hasCurl);
    }

    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    public static PingResult? ParsePing(string line)
    {
        var parts = line.Split('|');
        if (parts.Length < 3) return null;
        string label = PingTargets.FirstOrDefault(t => t.Target == parts[0]).Label ?? parts[0];
        var loss = LossPattern().Match(parts[1]);
        var average = AveragePattern().Match(parts[2]);
        // No loss figure at all means ping could not run, which says nothing about the network.
        return new PingResult(parts[0], label, loss.Success ? Number(loss.Groups[1].Value) : null, average.Success ? Number(average.Groups[1].Value) : null);
    }

    public static HttpResult? ParseHttp(string line)
    {
        int bar = line.IndexOf('|');
        if (bar < 0) return null;
        string url = line[..bar];
        string label = HttpTargets.FirstOrDefault(t => t.Url == url).Label ?? url;
        var fields = line[(bar + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4 || !int.TryParse(fields[3], out int status) || status == 0)
            return new HttpResult(url, label, null, null, null, null);
        double? Ms(string text) => Number(text) is { } seconds ? seconds * 1000 : null;
        return new HttpResult(url, label, Ms(fields[0]), Ms(fields[1]), Ms(fields[2]), status);
    }

    /// <summary>Reads a row of <c>ss -tuln</c> (Netid State Recv-Q Send-Q Local Peer) or <c>netstat -tuln</c> (Proto Recv-Q Send-Q Local Foreign State).</summary>
    public static ListeningPort? ParsePort(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5) return null;
        string protocol = fields[0].ToLowerInvariant();
        if (!protocol.StartsWith("tcp", StringComparison.Ordinal) && !protocol.StartsWith("udp", StringComparison.Ordinal)) return null;
        // netstat has a queue length where ss has the socket state.
        string local = int.TryParse(fields[1], out _) ? fields[3] : fields[4];
        int colon = local.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(local[(colon + 1)..], out int port)) return null;
        string address = local[..colon].Trim('[', ']');
        var process = ProcessPattern().Match(line);
        return new ListeningPort(protocol.StartsWith("tcp", StringComparison.Ordinal) ? "TCP" : "UDP", address, port, process.Success ? process.Groups[1].Value : "");
    }

    /// <summary>Time to open a TCP connection, which is what a user feels as latency to the server.</summary>
    public static async Task<double?> MeasureConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        using var client = new TcpClient();
        var watch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, linked.Token);
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }
}
