using EasyTerminal.Controls;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Monitoring;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Sessions;

/// <summary>
/// A tab with one device's status: live performance over a single long-lived SSH session,
/// then its network and the monitoring tools it has.
/// </summary>
public sealed class MonitorPane : UserControl, ISessionPanel
{
    private readonly AppServices _app = AppServices.Current;
    private readonly Device _device;
    private readonly NetworkSection _network;
    private readonly ToolsSection _tools;
    private readonly TextBlock _facts = Ui.Secondary("", true);
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false, Title = "监控已停止" };
    private readonly TextBlock _waiting = Ui.Secondary("正在连接，等待第一次采样…");
    private readonly Grid _tiles = new() { ColumnSpacing = 10 };
    private readonly MetricChart _cpuChart = new("CPU 使用率", HostMonitor.Capacity, HostMonitor.IntervalSeconds);
    private readonly MetricChart _memoryChart = new("内存使用率", HostMonitor.Capacity, HostMonitor.IntervalSeconds);
    private readonly MetricChart _networkChart = new("网络流量", HostMonitor.Capacity, HostMonitor.IntervalSeconds);
    private readonly MetricChart _diskChart = new("磁盘读写", HostMonitor.Capacity, HostMonitor.IntervalSeconds);
    private readonly StackPanel _disks = new() { Spacing = 8 };
    private readonly StackPanel _processes = new() { Spacing = 4 };
    private readonly (TextBlock Value, TextBlock Detail)[] _tileText;
    private readonly TextBlock _noDisks = Ui.Secondary("没有数据");
    private readonly TextBlock _noProcesses = Ui.Secondary("没有数据");
    private HostMonitor? _monitor;
    private bool _active;
    private string _lastStatus = "";

    public event Action? StateChanged;

    public bool IsLive => _monitor is { IsRunning: true };
    public string StatusText => _monitor?.Error != null ? "已停止" : IsLive ? $"每 {HostMonitor.IntervalSeconds} 秒采样" : "未连接";

    public MonitorPane(SessionManager sessions, Device device)
    {
        _device = device;
        _network = new NetworkSection(device) { Margin = new Thickness(0, 12, 0, 0) };
        _tools = new ToolsSection(sessions, device) { Margin = new Thickness(0, 12, 0, 0) };
        var (root, panel) = Ui.Page();
        Content = root;

        var header = Ui.Columns("*", "auto");
        var title = new StackPanel();
        title.Children.Add(Ui.Title("状态"));
        title.Children.Add(_facts);
        header.Children.Add(title.InColumn(0));
        var restart = Ui.Button("重新开始", Ui.GlyphRefresh, Restart);
        restart.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(restart.InColumn(1));
        panel.Children.Add(header);
        panel.Children.Add(_error);
        panel.Children.Add(_waiting);

        string[] names = { "CPU", "内存", "负载", "网络" };
        _tileText = new (TextBlock, TextBlock)[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            _tiles.ColumnDefinitions.Add(new ColumnDefinition());
            var tile = new StackPanel { Spacing = 2 };
            tile.Children.Add(Ui.Secondary(names[i]));
            var value = new TextBlock { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = Ui.Secondary("");
            tile.Children.Add(value);
            tile.Children.Add(detail);
            _tileText[i] = (value, detail);
            _tiles.Children.Add(Ui.Card(tile, 12).InColumn(i));
        }
        panel.Children.Add(_tiles);

        var charts = new CardGridPanel { MinCardWidth = 340, Spacing = 16 };
        charts.Children.Add(_cpuChart);
        charts.Children.Add(_memoryChart);
        charts.Children.Add(_networkChart);
        charts.Children.Add(_diskChart);
        panel.Children.Add(charts);

        var lower = new CardGridPanel { MinCardWidth = 340, Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };
        var diskPanel = new StackPanel { Spacing = 6 };
        diskPanel.Children.Add(Ui.Strong("磁盘空间"));
        diskPanel.Children.Add(_noDisks);
        diskPanel.Children.Add(_disks);
        lower.Children.Add(diskPanel);
        var processPanel = new StackPanel { Spacing = 6 };
        processPanel.Children.Add(Ui.Strong("占用最高的进程"));
        processPanel.Children.Add(_noProcesses);
        processPanel.Children.Add(_processes);
        lower.Children.Add(processPanel);
        panel.Children.Add(lower);
        panel.Children.Add(_network);
        panel.Children.Add(_tools);
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active) Update();
    }

    public void Restart()
    {
        _monitor?.Dispose();
        var monitor = new HostMonitor(_app.Ssh, _device);
        monitor.Updated += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (monitor != _monitor) return;
            if (_active) Update();
            // Each sample would otherwise rebuild the sidebar entry and whatever page is open.
            if (StatusText == _lastStatus) return;
            _lastStatus = StatusText;
            StateChanged?.Invoke();
        });
        _monitor = monitor;
        monitor.Start();
        _network.Restart();
        Update();
        StateChanged?.Invoke();
    }

#if DEBUG
    internal void PreviewForTest(IEnumerable<MonitorSample> samples)
    {
        _monitor?.Dispose();
        _monitor = new HostMonitor(_app.Ssh, _device);
        _monitor.Seed(samples);
        Update();
    }
#endif

    private static string FormatRate(double bytesPerSecond)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double value = bytesPerSecond;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[0]}" : $"{value:0.#} {units[unit]}";
    }

    private void Update()
    {
        var monitor = _monitor;
        if (monitor == null) return;
        _facts.Text = _device.Facts != null
            ? Ui.DescribeFacts(_device.Facts) + (Ui.DescribeLocation(_device.Facts) is { Length: > 0 } location ? " · " + location : "")
            : _device.Host;
        _error.Message = monitor.Error ?? "";
        _error.IsOpen = monitor.Error != null;

        var samples = monitor.Samples;
        bool hasData = samples.Count > 0;
        _waiting.Visibility = !hasData && monitor.Error == null ? Visibility.Visible : Visibility.Collapsed;
        if (!hasData) return;

        var latest = samples[^1];
        _tileText[0].Value.Text = latest.CpuPercent is { } cpu ? $"{cpu:0}%" : "—";
        _tileText[0].Detail.Text = latest.CpuPercent != null ? (_device.Facts?.CpuCount > 0 ? $"{_device.Facts.CpuCount} 核" : "") : "等待下一次采样";
        _tileText[1].Value.Text = latest.MemoryPercent is { } memory ? $"{memory:0}%" : "—";
        _tileText[1].Detail.Text = latest.MemoryTotalKb > 0
            ? $"{Ui.FormatSize((latest.MemoryTotalKb - latest.MemoryAvailableKb) * 1024)} / {Ui.FormatSize(latest.MemoryTotalKb * 1024)}" + (latest.SwapTotalKb > 0 ? $" · 交换 {Ui.FormatSize((latest.SwapTotalKb - latest.SwapFreeKb) * 1024)}" : "")
            : "此系统不提供";
        _tileText[2].Value.Text = latest.Load1.ToString("0.00");
        _tileText[2].Detail.Text = $"5 分钟 {latest.Load5:0.00} · 15 分钟 {latest.Load15:0.00}" + (latest.Tasks.Length > 0 ? $" · 进程 {latest.Tasks}" : "");
        _tileText[3].Value.Text = latest.NetworkRxBytesPerSecond is { } rx ? "↓ " + FormatRate(rx) : "—";
        _tileText[3].Detail.Text = latest.NetworkTxBytesPerSecond is { } tx ? "↑ " + FormatRate(tx) : "";

        _cpuChart.SetData(new[] { new ChartSeries("CPU", samples.Select(s => s.CpuPercent).ToList()) }, v => $"{v:0}%", 100);
        _memoryChart.SetData(new[] { new ChartSeries("内存", samples.Select(s => s.MemoryPercent).ToList()) }, v => $"{v:0}%", 100);
        _networkChart.SetData(new[]
        {
            new ChartSeries("接收", samples.Select(s => s.NetworkRxBytesPerSecond).ToList()),
            new ChartSeries("发送", samples.Select(s => s.NetworkTxBytesPerSecond).ToList()),
        }, FormatRate);
        _diskChart.SetData(new[]
        {
            new ChartSeries("读取", samples.Select(s => s.DiskReadBytesPerSecond).ToList()),
            new ChartSeries("写入", samples.Select(s => s.DiskWriteBytesPerSecond).ToList()),
        }, FormatRate);

        var disks = latest.Disks.OrderByDescending(d => d.Percent).Take(6).ToList();
        var diskRows = Rows(_disks, disks.Count, () =>
        {
            var row = new StackPanel { Spacing = 2 };
            var label = Ui.Columns("*", "auto");
            label.Children.Add(Ui.Body("").InColumn(0));
            label.Children.Add(Ui.Secondary("").InColumn(1));
            row.Children.Add(label);
            row.Children.Add(new ProgressBar());
            return row;
        });
        for (int i = 0; i < disks.Count; i++)
        {
            var disk = disks[i];
            var label = (Grid)diskRows[i].Children[0];
            bool nearlyFull = disk.Percent >= 90;
            ((TextBlock)label.Children[0]).Text = disk.Mount;
            ((TextBlock)label.Children[1]).Text = $"{(nearlyFull ? "空间不足 · " : "")}{disk.Percent:0}% · {Ui.FormatSize(disk.UsedKb * 1024)} / {Ui.FormatSize(disk.SizeKb * 1024)}";
            var bar = (ProgressBar)diskRows[i].Children[1];
            bar.Value = disk.Percent;
            bar.ShowError = nearlyFull;
        }
        _noDisks.Visibility = disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var processRows = Rows(_processes, latest.Processes.Count, () =>
        {
            var row = Ui.Columns("*", "80", "80");
            row.Children.Add(Ui.Body("").InColumn(0));
            var cpuText = Ui.Secondary("");
            cpuText.TextAlignment = TextAlignment.Right;
            row.Children.Add(cpuText.InColumn(1));
            var memoryText = Ui.Secondary("");
            memoryText.TextAlignment = TextAlignment.Right;
            row.Children.Add(memoryText.InColumn(2));
            return row;
        });
        for (int i = 0; i < latest.Processes.Count; i++)
        {
            var process = latest.Processes[i];
            ((TextBlock)processRows[i].Children[0]).Text = process.Name;
            ((TextBlock)processRows[i].Children[1]).Text = $"CPU {process.Cpu:0.#}%";
            ((TextBlock)processRows[i].Children[2]).Text = $"内存 {process.Memory:0.#}%";
        }
        _noProcesses.Visibility = latest.Processes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Keeps exactly <paramref name="count"/> rows, reusing the ones already shown so their values change in place.</summary>
    private static List<T> Rows<T>(StackPanel host, int count, Func<T> create) where T : Panel
    {
        var rows = host.Children.OfType<T>().ToList();
        while (rows.Count < count)
        {
            var row = create();
            host.Children.Add(row);
            rows.Add(row);
        }
        while (rows.Count > count)
        {
            host.Children.Remove(rows[^1]);
            rows.RemoveAt(rows.Count - 1);
        }
        return rows;
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        _network.Dispose();
        _tools.Dispose();
    }
}
