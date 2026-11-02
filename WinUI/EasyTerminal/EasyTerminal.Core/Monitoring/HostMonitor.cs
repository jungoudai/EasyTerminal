using System.Globalization;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;

namespace EasyTerminal.Core.Monitoring;

public sealed record DiskUsage(string Mount, long SizeKb, long UsedKb)
{
    public double Percent => SizeKb > 0 ? 100.0 * UsedKb / SizeKb : 0;
}

public sealed record ProcessUsage(string Name, double Cpu, double Memory);

/// <summary>One reading. Null means this host cannot report the metric, or a rate has no earlier reading yet.</summary>
public sealed record MonitorSample(
    DateTimeOffset Time,
    double? CpuPercent,
    double Load1, double Load5, double Load15,
    string Tasks,
    long MemoryTotalKb, long MemoryAvailableKb, long SwapTotalKb, long SwapFreeKb,
    double? NetworkRxBytesPerSecond, double? NetworkTxBytesPerSecond,
    double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond,
    IReadOnlyList<DiskUsage> Disks,
    IReadOnlyList<ProcessUsage> Processes)
{
    public double? MemoryPercent => MemoryTotalKb > 0 ? 100.0 * (MemoryTotalKb - MemoryAvailableKb) / MemoryTotalKb : null;
}

/// <summary>Turns the monitor script's output into samples. Rates come from the difference between readings.</summary>
public sealed class MonitorParser
{
    private const int SectorSize = 512;

    private double? _uptime, _previousUptime;
    private long[]? _cpu, _previousCpu;
    private long? _rx, _tx, _previousRx, _previousTx, _read, _write, _previousRead, _previousWrite;
    private double _load1, _load5, _load15;
    private string _tasks = "";
    private long _memTotal, _memAvailable, _swapTotal, _swapFree;
    private List<DiskUsage> _disks = new();
    private List<DiskUsage>? _pendingDisks;
    private List<ProcessUsage> _processes = new();

    /// <summary>Feeds one line; returns a sample when the line completes a reading.</summary>
    public MonitorSample? Feed(string line)
    {
        int space = line.IndexOf(' ');
        string tag = space < 0 ? line : line[..space];
        string[] fields = space < 0 ? Array.Empty<string>() : line[(space + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (tag)
        {
            case "T":
                if (fields.Length > 0 && Number(fields[0]) is { } uptime) _uptime = uptime;
                break;
            case "C":
                _cpu = fields.Select(f => long.TryParse(f, out long v) ? v : 0).ToArray();
                break;
            case "L":
                if (fields.Length >= 3)
                {
                    _load1 = Number(fields[0]) ?? 0;
                    _load5 = Number(fields[1]) ?? 0;
                    _load15 = Number(fields[2]) ?? 0;
                }
                _tasks = fields.Length >= 4 ? fields[3] : "";
                break;
            case "M":
                if (fields.Length >= 4)
                {
                    _memTotal = Whole(fields[0]);
                    _memAvailable = Whole(fields[1]);
                    _swapTotal = Whole(fields[2]);
                    _swapFree = Whole(fields[3]);
                }
                break;
            case "N":
                if (fields.Length >= 2)
                {
                    _rx = Whole(fields[0]);
                    _tx = Whole(fields[1]);
                }
                break;
            case "I":
                if (fields.Length >= 2)
                {
                    _read = Whole(fields[0]);
                    _write = Whole(fields[1]);
                }
                break;
            case "D":
                if (fields.Length >= 3) (_pendingDisks ??= new()).Add(new DiskUsage(string.Join(' ', fields[2..]), Whole(fields[0]), Whole(fields[1])));
                break;
            case "P":
                if (fields.Length >= 3) _processes.Add(new ProcessUsage(string.Join(' ', fields[2..]), Number(fields[0]) ?? 0, Number(fields[1]) ?? 0));
                break;
            case "E":
                return Complete();
        }
        return null;
    }

    private MonitorSample Complete()
    {
        if (_pendingDisks != null)
        {
            // df only runs every so often, so the last list stays valid in between.
            _disks = _pendingDisks;
            _pendingDisks = null;
        }

        double? elapsed = _uptime != null && _previousUptime != null && _uptime > _previousUptime ? _uptime - _previousUptime : null;
        var sample = new MonitorSample(
            DateTimeOffset.Now,
            CpuPercent(_previousCpu, _cpu),
            _load1, _load5, _load15, _tasks,
            _memTotal, _memAvailable, _swapTotal, _swapFree,
            Rate(_previousRx, _rx, elapsed, 1), Rate(_previousTx, _tx, elapsed, 1),
            Rate(_previousRead, _read, elapsed, SectorSize), Rate(_previousWrite, _write, elapsed, SectorSize),
            _disks, _processes);

        _previousUptime = _uptime;
        _previousCpu = _cpu;
        _previousRx = _rx;
        _previousTx = _tx;
        _previousRead = _read;
        _previousWrite = _write;
        _processes = new();
        return sample;
    }

    /// <summary>Busy share between two /proc/stat readings; idle and iowait are the 4th and 5th fields.</summary>
    public static double? CpuPercent(long[]? previous, long[]? current)
    {
        if (previous == null || current == null || previous.Length < 4 || current.Length != previous.Length) return null;
        long total = 0, idle = 0;
        // guest and guest_nice (fields 9 and 10) are already counted in user and nice.
        for (int i = 0; i < Math.Min(current.Length, 8); i++)
        {
            long delta = Math.Max(0, current[i] - previous[i]);
            total += delta;
            if (i is 3 or 4) idle += delta;
        }
        return total > 0 ? Math.Clamp(100.0 * (total - idle) / total, 0, 100) : null;
    }

    private static double? Rate(long? previous, long? current, double? elapsed, int unit)
    {
        // A counter that went backwards was reset, which says nothing about traffic.
        if (previous == null || current == null || elapsed == null || current < previous) return null;
        return (current.Value - previous.Value) * (double)unit / elapsed.Value;
    }

    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    private static long Whole(string text) => long.TryParse(text, out long value) ? value : 0;
}

/// <summary>
/// Samples one host over a single long-lived SSH session, so a hardware key or password is only needed once.
/// Events fire on a background thread.
/// </summary>
public sealed class HostMonitor : IDisposable
{
    public const int Capacity = 90;
    public const int IntervalSeconds = 2;

    // One self-contained line per metric, so a tool missing on this host only leaves that metric empty.
    public static readonly string Script =
        "export LC_ALL=C; i=0; while :; do " +
        "[ -r /proc/uptime ] && echo \"T $(cut -d' ' -f1 /proc/uptime)\"; " +
        "[ -r /proc/stat ] && sed -n 's/^cpu  */C /p' /proc/stat; " +
        "if [ -r /proc/loadavg ]; then echo \"L $(cut -d' ' -f1-4 /proc/loadavg)\"; else echo \"L $(uptime | sed 's/.*load averages*: //;s/,//g')\"; fi; " +
        "[ -r /proc/meminfo ] && awk '/^MemTotal:/{t=$2}/^MemAvailable:/{a=$2}/^SwapTotal:/{s=$2}/^SwapFree:/{f=$2}END{print \"M\",t+0,a+0,s+0,f+0}' /proc/meminfo; " +
        // Bridges and virtual interfaces carry the same traffic as the physical one and would count it twice.
        "[ -r /proc/net/dev ] && awk 'NR>2{sub(/^ +/,\"\");split($0,p,\":\");n=p[1];if(n~/^(lo|veth|docker|br-|virbr|vmbr|tap|fw|cni|flannel)/)next;split(p[2],v,\" \");r+=v[1];t+=v[9]}END{print \"N\",r+0,t+0}' /proc/net/dev; " +
        "[ -r /proc/diskstats ] && awk '$3~/^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme[0-9]+n[0-9]+|mmcblk[0-9]+)$/{r+=$6;w+=$10}END{print \"I\",r+0,w+0}' /proc/diskstats; " +
        // df can stall on a sick mount, so it stays local and infrequent.
        "if [ $((i%15)) = 0 ]; then df -kPl 2>/dev/null | awk 'NR>1&&$1!~/^(tmpfs|devtmpfs|overlay|none|udev|shm)/&&$6!~/^\\/(proc|sys|dev|run|snap|boot\\/efi)/{print \"D\",$2,$3,$6}'; fi; " +
        "ps -eo pcpu,pmem,comm --sort=-pcpu 2>/dev/null | awk 'NR>1&&NR<8{print \"P\",$1,$2,$3}'; " +
        $"echo E; i=$((i+1)); sleep {IntervalSeconds}; done";

    private readonly SshRunner _runner;
    private readonly Device _device;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly List<MonitorSample> _samples = new();

    public HostMonitor(SshRunner runner, Device device)
    {
        _runner = runner;
        _device = device;
    }

    public Device Device => _device;
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public event Action? Updated;

    public IReadOnlyList<MonitorSample> Samples
    {
        get
        {
            lock (_lock) return _samples.ToArray();
        }
    }

    /// <summary>Fills the history without a connection, for previews.</summary>
    public void Seed(IEnumerable<MonitorSample> samples)
    {
        lock (_lock) _samples.AddRange(samples);
    }

    public void Start()
    {
        IsRunning = true;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            using var context = await _runner.PrepareAsync(_device, null, false, null, _stop.Token);
            using var process = _runner.StartBackground(context, "-T", context.Alias, "sh -c " + ShellQuote.Sh(Script));
            using var killOnStop = _stop.Token.Register(() =>
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }
            });
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync();
            var parser = new MonitorParser();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (parser.Feed(line.TrimEnd('\r')) is not { } sample) continue;
                lock (_lock)
                {
                    _samples.Add(sample);
                    if (_samples.Count > Capacity) _samples.RemoveAt(0);
                }
                Updated?.Invoke();
            }
            if (!_stop.IsCancellationRequested)
            {
                string message = (await errors).Trim();
                Error = message.Length > 0 ? message : "SSH 连接已断开";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested) Error = ex.Message;
        }
        IsRunning = false;
        if (!_stop.IsCancellationRequested) Updated?.Invoke();
    }

    public void Dispose() => _stop.Cancel();
}
