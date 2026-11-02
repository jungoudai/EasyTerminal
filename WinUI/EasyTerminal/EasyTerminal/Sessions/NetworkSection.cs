using EasyTerminal.Controls;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Monitoring;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Sessions;

/// <summary>
/// The network part of a device's status: latency from this computer, how the server reaches the outside world,
/// what it listens on, and its interfaces.
/// </summary>
public sealed class NetworkSection : StackPanel, IDisposable
{
    private const int Samples = 90;
    private const int IntervalSeconds = 2;

    private readonly AppServices _app = AppServices.Current;
    private readonly Device _device;
    private readonly MetricChart _latencyChart = new("本机到服务器的延迟（TCP 连接）", Samples, IntervalSeconds);
    private readonly TextBlock _latencySummary = Ui.Secondary("");
    private readonly StackPanel _outbound = new() { Spacing = 6 };
    private readonly StackPanel _ports = new() { Spacing = 4 };
    private readonly StackPanel _interfaces = new() { Spacing = 2 };
    private readonly ProgressBar _running = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Button _rerun;
    private readonly List<double?> _latency = new();
    private CancellationTokenSource? _stop;
    private bool _testing;

    public NetworkSection(Device device)
    {
        _device = device;
        Spacing = 8;

        var header = Ui.Columns("*", "auto");
        var title = Ui.Section("网络");
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title.InColumn(0));
        _rerun = Ui.Button("重新检测", Ui.GlyphRefresh, () => _ = RunChecksAsync());
        header.Children.Add(_rerun.InColumn(1));
        Children.Add(header);
        Children.Add(_running);

        Children.Add(_latencyChart);
        Children.Add(_latencySummary);

        Children.Add(Ui.Strong("服务器访问外网"));
        Children.Add(_outbound);

        var lower = new CardGridPanel { MinCardWidth = 340, Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };
        var portPanel = new StackPanel { Spacing = 6 };
        portPanel.Children.Add(Ui.Strong("监听端口"));
        portPanel.Children.Add(_ports);
        lower.Children.Add(portPanel);
        var interfacePanel = new StackPanel { Spacing = 6 };
        interfacePanel.Children.Add(Ui.Strong("网络接口"));
        interfacePanel.Children.Add(_interfaces);
        lower.Children.Add(interfacePanel);
        Children.Add(lower);
    }

    /// <summary>Starts measuring latency again and reruns the server-side checks.</summary>
    public void Restart()
    {
        _stop?.Cancel();
        _stop = new CancellationTokenSource();
        _latency.Clear();
        _ = MeasureLatencyAsync(_stop.Token);
        _ = RunChecksAsync();
    }

    private async Task MeasureLatencyAsync(CancellationToken cancellation)
    {
        // Behind a jump host or proxy this computer can't reach the server directly, so there is nothing honest to time.
        if (!_device.Network.IsDirect)
        {
            _latencySummary.Text = "这台设备经跳板或代理连接，无法从本机直接测量延迟。";
            _latencyChart.Visibility = Visibility.Collapsed;
            return;
        }
        var endpoint = HostEndpoint.AllFor(_device)[0];
        int port = endpoint.Port ?? _device.Port;
        while (!cancellation.IsCancellationRequested)
        {
            double? ms = await Task.Run(() => NetworkDiagnostics.MeasureConnectAsync(endpoint.Host, port, TimeSpan.FromSeconds(3), cancellation));
            if (cancellation.IsCancellationRequested) return;
            _latency.Add(ms);
            if (_latency.Count > Samples) _latency.RemoveAt(0);
            _latencyChart.SetData(new[] { new ChartSeries("延迟", _latency.ToList()) }, v => $"{v:0} ms");
            var received = _latency.Where(v => v != null).Select(v => v!.Value).ToList();
            double loss = 100.0 * (_latency.Count - received.Count) / _latency.Count;
            _latencySummary.Text = $"{endpoint.Host}:{port} · 当前 {(ms is { } now ? $"{now:0} ms" : "超时")} · 平均 {(received.Count > 0 ? $"{received.Average():0} ms" : "—")} · 最低 {(received.Count > 0 ? $"{received.Min():0} ms" : "—")} · 失败 {loss:0}%";
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunChecksAsync()
    {
        if (_testing) return;
        _testing = true;
        foreach (var section in new[] { _outbound, _ports, _interfaces })
        {
            section.Children.Clear();
            section.Children.Add(Ui.Secondary("正在检测…"));
        }
        _running.Visibility = Visibility.Visible;
        _rerun.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _app.Ssh.RunRemoteAsync(_device, null, NetworkDiagnostics.Script, TimeSpan.FromSeconds(90)));
            if (!result.Output.Contains("@@END", StringComparison.Ordinal))
                throw new IOException(result.Error.Trim().Length > 0 ? result.Error.Trim() : $"ssh 退出代码 {result.ExitCode}");
            Show(NetworkDiagnostics.Parse(result.Output));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _outbound.Children.Clear();
            _outbound.Children.Add(new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = true, IsClosable = false, Title = "无法检测", Message = ex.Message });
            foreach (var section in new[] { _ports, _interfaces })
            {
                section.Children.Clear();
                section.Children.Add(Ui.Secondary("没有数据"));
            }
        }
        _testing = false;
        _running.Visibility = Visibility.Collapsed;
        _rerun.IsEnabled = true;
    }

    private void Show(NetworkReport report)
    {
        _outbound.Children.Clear();
        foreach (var ping in report.Pings)
        {
            string detail = ping.LossPercent == null ? "无法 ping（可能没有权限或被防火墙拦截）"
                : ping.LossPercent >= 100 ? "不通"
                : $"平均 {ping.AverageMs:0.#} ms" + (ping.LossPercent > 0 ? $" · 丢包 {ping.LossPercent:0}%" : "");
            _outbound.Children.Add(ResultRow("Ping " + ping.Label, ping.Target, detail, ping.LossPercent is < 100));
        }
        if (!report.HasCurl) _outbound.Children.Add(Ui.Secondary("服务器没有 curl，跳过了网站访问测试。"));
        foreach (var http in report.Http)
        {
            string detail = http.Status == null ? "无法访问"
                : $"HTTP {http.Status} · DNS {http.DnsMs:0} ms · 连接 {http.ConnectMs:0} ms · 首字节 {http.FirstByteMs:0} ms";
            _outbound.Children.Add(ResultRow(http.Label, http.Url, detail, http.Status != null));
        }

        _ports.Children.Clear();
        if (report.Ports.Count == 0) _ports.Children.Add(Ui.Secondary("没有数据（需要 ss 或 netstat）"));
        foreach (var port in report.Ports)
        {
            var row = Ui.Columns("50", "70", "*");
            row.Children.Add(Ui.Secondary(port.Protocol).InColumn(0));
            row.Children.Add(Ui.Body(port.Port.ToString()).InColumn(1));
            row.Children.Add(Ui.Secondary(port.Address + (port.Process.Length > 0 ? " · " + port.Process : "")).InColumn(2));
            _ports.Children.Add(row);
        }

        _interfaces.Children.Clear();
        if (report.Interfaces.Count == 0) _interfaces.Children.Add(Ui.Secondary("没有数据"));
        foreach (var line in report.Interfaces)
            _interfaces.Children.Add(new TextBlock { Text = line, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
    }

    private static FrameworkElement ResultRow(string name, string target, string detail, bool ok)
    {
        var row = Ui.Columns("auto", "160", "*");
        var mark = Ui.Icon(ok ? Ui.GlyphAccept : Ui.GlyphClose, 12);
        mark.Foreground = Ui.Brush(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        row.Children.Add(mark.InColumn(0));
        var label = Ui.Body(name);
        ToolTipService.SetToolTip(label, target);
        row.Children.Add(label.InColumn(1));
        row.Children.Add(Ui.Secondary(detail).InColumn(2));
        return row;
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _stop = null;
    }
}
