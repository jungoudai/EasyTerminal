using EasyTerminal.Controls;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Monitoring;
using EasyTerminal.Core.Tailscale;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages.Home;

/// <summary>The body of one home panel. Refresh rebuilds it from app state; Loaded and Unloaded bound any live work.</summary>
public abstract class HomeWidgetView : IDisposable
{
    protected readonly AppServices App = AppServices.Current;
    protected readonly SessionManager Sessions;
    protected readonly StackPanel Panel = new() { Spacing = 6 };
    private string? _signature;

    protected HomeWidgetView(HomeWidget model, SessionManager sessions)
    {
        Model = model;
        Sessions = sessions;
    }

    public HomeWidget Model { get; }
    public FrameworkElement Body => Panel;
    public string Title => Model.Title.Length > 0 ? Model.Title : DefaultTitle;
    public abstract string DefaultTitle { get; }
    public abstract string Glyph { get; }
    public virtual FrameworkElement? HeaderAction => null;
    public virtual bool UsesGroup => false;
    public virtual bool UsesDevice => false;
    public virtual bool UsesMaxItems => false;
    public bool HasOptions => true;

    /// <summary>Rebuilds the body unless the state it shows is unchanged since the last time.</summary>
    public void Refresh()
    {
        // One broken panel shows its error in its own card instead of taking the whole page down.
        try
        {
            string? signature = Signature();
            if (signature != null && signature == _signature) return;
            _signature = signature;
            Panel.Children.Clear();
            Build();
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _signature = null;
            Error = ex.ToString();
            Panel.Children.Clear();
            Panel.Children.Add(Ui.Secondary("此组件出错：" + ex.Message, true));
        }
    }

    /// <summary>The last failure while building the body, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Forces the next refresh to rebuild, as after settings change.</summary>
    public void Invalidate() => _signature = null;

    /// <summary>A summary of what the body shows; null rebuilds on every refresh.</summary>
    protected virtual string? Signature() => null;
    protected abstract void Build();
    public virtual void Loaded() { }
    public virtual void Unloaded() { }
    public virtual void Dispose() => Unloaded();

    protected IEnumerable<Device> FilteredDevices() =>
        App.Store.Data.Devices.Where(d => Model.Group.Length == 0 || d.Group == Model.Group);

    protected static void Navigate(string page) => EasyTerminal.App.MainWindowInstance?.Navigate(page);

    protected static FrameworkElement Empty(string message, string? buttonText = null, string? glyph = null, Action? action = null)
    {
        var row = Ui.Columns("*", "auto");
        var text = Ui.Secondary(message, true);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text.InColumn(0));
        if (buttonText != null && action != null) row.Children.Add(Ui.Button(buttonText, glyph, action).InColumn(1));
        return row;
    }

    /// <summary>A flat full-width button holding a row of content, for lists whose rows open something.</summary>
    protected static Button RowButton(FrameworkElement content, Action onClick, string? tooltip = null)
    {
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        if (tooltip != null) ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    protected static Connection? PrimaryConnection(Device device) =>
        device.Connections.Where(device.Shows).FirstOrDefault(c => c.Protocol == ConnectionProtocol.Ssh)
        ?? device.Connections.Where(device.Shows).FirstOrDefault(c => c.Protocol is not (ConnectionProtocol.Monitor or ConnectionProtocol.Docker or ConnectionProtocol.Proxmox))
        ?? device.Connections.FirstOrDefault(device.Shows);

    protected static ProgressBar Meter(double percent) => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = Math.Clamp(percent, 0, 100),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Ui.Brush(percent >= 90 ? "SystemFillColorCriticalBrush" : percent >= 70 ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush"),
    };

    public static HomeWidgetView Create(HomeWidget model, SessionManager sessions) => model.Kind switch
    {
        HomeWidgetKind.Overview => new OverviewWidget(model, sessions),
        HomeWidgetKind.Sessions => new SessionsWidget(model, sessions),
        HomeWidgetKind.Devices => new DevicesWidget(model, sessions),
        HomeWidgetKind.Recent => new RecentWidget(model, sessions),
        HomeWidgetKind.Resources => new ResourcesWidget(model, sessions),
        HomeWidgetKind.LiveMonitor => new LiveMonitorWidget(model, sessions),
        HomeWidgetKind.Forwards => new ForwardsWidget(model, sessions),
        HomeWidgetKind.Environment => new EnvironmentWidget(model, sessions),
        HomeWidgetKind.LocalShells => new LocalShellsWidget(model, sessions),
        HomeWidgetKind.Tailscale => new TailscaleWidget(model, sessions),
        _ => new OverviewWidget(model, sessions),
    };

    /// <summary>Name, icon and a one-line description of each kind, for the add menu.</summary>
    public static readonly (HomeWidgetKind Kind, string Name, string Glyph, int Width)[] Catalog =
    {
        (HomeWidgetKind.Overview, "概览", Ui.GlyphChart, 12),
        (HomeWidgetKind.Sessions, "活跃连接", Ui.GlyphSessions, 8),
        (HomeWidgetKind.Devices, "机器", Ui.GlyphDevices, 12),
        (HomeWidgetKind.Recent, "最近使用", Ui.GlyphStar, 4),
        (HomeWidgetKind.Resources, "资源数据", Ui.GlyphResources, 8),
        (HomeWidgetKind.LiveMonitor, "实时监控", Ui.GlyphChart, 6),
        (HomeWidgetKind.Forwards, "端口转发", Ui.GlyphForward, 6),
        (HomeWidgetKind.Environment, "本机环境", Ui.GlyphComputer, 12),
        (HomeWidgetKind.LocalShells, "本地终端", Ui.GlyphTerminal, 4),
        (HomeWidgetKind.Tailscale, "Tailscale", Ui.GlyphNetwork, 6),
    };

    protected static string NameOf(HomeWidgetKind kind) => Catalog.First(c => c.Kind == kind).Name;
    protected static string GlyphOf(HomeWidgetKind kind) => Catalog.First(c => c.Kind == kind).Glyph;
}

/// <summary>Counts that link to the pages behind them.</summary>
internal sealed class OverviewWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    public override string DefaultTitle => NameOf(HomeWidgetKind.Overview);
    public override string Glyph => GlyphOf(HomeWidgetKind.Overview);

    protected override string? Signature()
    {
        var d = App.Store.Data;
        return $"{Sessions.Tabs.Count}|{Sessions.Tabs.Count(t => t.IsLive)}|{d.Devices.Count}|{d.Devices.Count(x => x.Facts != null)}|{d.PortForwards.Count(App.IsForwardRunning)}|{d.PortForwards.Count}|{d.Snippets.Count}|{TailscaleText()}";
    }

    private static string TailscaleText() =>
        !TailscaleNode.Shared.IsHelperAvailable ? "未包含" : TailscaleNode.Shared.IsStarting ? "启动中" : TailscaleNode.Shared.IsStarted ? "已启动" : "未启动";

    protected override void Build()
    {
        var d = App.Store.Data;
        var grid = new CardGridPanel { MinCardWidth = 150, Spacing = 8 };
        grid.Children.Add(Tile("会话", Sessions.Tabs.Count.ToString(), $"{Sessions.Tabs.Count(t => t.IsLive)} 个在线", null));
        grid.Children.Add(Tile("设备", d.Devices.Count.ToString(), $"{d.Devices.Count(x => x.Facts != null)} 台已扫描", "devices"));
        grid.Children.Add(Tile("端口转发", $"{d.PortForwards.Count(App.IsForwardRunning)}/{d.PortForwards.Count}", "运行中 / 全部", "forwards"));
        grid.Children.Add(Tile("代码片段", d.Snippets.Count.ToString(), "可在终端中一键发送", "snippets"));
        grid.Children.Add(Tile("Tailscale", TailscaleText(), "内置节点", "settings"));
        Panel.Children.Add(grid);
    }

    private static FrameworkElement Tile(string label, string value, string detail, string? page)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(Ui.Secondary(label));
        content.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(Ui.Secondary(detail));
        if (page == null) return Ui.Card(content, 12);
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12),
        };
        button.Click += (_, _) => Navigate(page);
        return button;
    }
}

/// <summary>Open sessions with switch, reconnect and close.</summary>
internal sealed class SessionsWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    public override string DefaultTitle => NameOf(HomeWidgetKind.Sessions);
    public override string Glyph => GlyphOf(HomeWidgetKind.Sessions);
    public override bool UsesMaxItems => true;

    protected override void Build()
    {
        if (Sessions.Tabs.Count == 0)
        {
            Panel.Children.Add(Empty("没有运行中的会话。", "前往设备", Ui.GlyphDevices, () => Navigate("devices")));
            return;
        }
        foreach (var tab in Sessions.Tabs.Take(Model.MaxItems))
        {
            var row = Ui.Columns("auto", "*", "auto", "auto", "auto");
            row.ColumnSpacing = 8;
            var icon = Ui.Icon(tab.Glyph);
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon.InColumn(0));
            var text = new StackPanel();
            text.Children.Add(Ui.Body(tab.Title));
            string origin = tab.Device?.Name ?? (tab.LocalTarget != null ? "本机 · " + Ui.Name(tab.LocalTarget.Kind) : "");
            text.Children.Add(Ui.Secondary($"{origin} · {tab.StatusText} · 启动于 {Ui.RelativeTime(tab.OpenedAt)}"));
            row.Children.Add(text.InColumn(1));
            var captured = tab;
            row.Children.Add(Ui.IconButton(Ui.GlyphPlay, "切换到此会话", () => EasyTerminal.App.MainWindowInstance?.ActivateTab(captured)).InColumn(2));
            row.Children.Add(Ui.IconButton(Ui.GlyphRefresh, "重新连接", () =>
            {
                captured.Restart();
                EasyTerminal.App.MainWindowInstance?.ActivateTab(captured);
            }).InColumn(3));
            row.Children.Add(Ui.IconButton(Ui.GlyphClose, "关闭", () => _ = CloseAsync(captured)).InColumn(4));
            Panel.Children.Add(row);
        }
        if (Sessions.Tabs.Count > Model.MaxItems) Panel.Children.Add(Ui.Secondary($"还有 {Sessions.Tabs.Count - Model.MaxItems} 个会话"));
    }

    private async Task CloseAsync(SessionTab tab)
    {
        if (!tab.HasForegroundWork || await Dialogs.ConfirmAsync($"关闭 {tab.Title}？", "连接或进程会被终止。", "关闭", destructive: true))
            Sessions.Close(tab);
    }
}

/// <summary>Machines as tiles; clicking one opens its main connection, the menu lists the rest.</summary>
internal sealed class DevicesWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    public override string DefaultTitle => Model.Group.Length > 0 ? "机器 · " + Model.Group : NameOf(HomeWidgetKind.Devices);
    public override string Glyph => GlyphOf(HomeWidgetKind.Devices);
    public override bool UsesGroup => true;

    protected override string? Signature() =>
        string.Join("|", FilteredDevices().Select(d => $"{d.Id}{d.Name}{d.Host}{d.Facts?.ScannedAt}{App.ProbingDevices.Contains(d.Id)}{d.Connections.Count}"));

    protected override void Build()
    {
        var devices = FilteredDevices().ToList();
        if (devices.Count == 0)
        {
            Panel.Children.Add(Empty(Model.Group.Length > 0 ? $"分组“{Model.Group}”里没有设备。" : "还没有设备。", "添加设备", Ui.GlyphAdd, () => Navigate("devices")));
            return;
        }
        var grid = new CardGridPanel { MinCardWidth = 230, Spacing = 8 };
        foreach (var device in devices) grid.Children.Add(Tile(device));
        Panel.Children.Add(grid);
    }

    private FrameworkElement Tile(Device device)
    {
        var facts = device.Facts;
        bool desktopOnly = device.Connections.Count > 0 && device.Connections.All(c => c.Protocol == ConnectionProtocol.Rdp);
        var icon = DevicesPage.MachineIcon(OsIdentity.LogoFor(facts) ?? (desktopOnly ? "windows" : null), Ui.GlyphServer,
            facts != null ? Ui.DescribeFacts(facts) : "尚未扫描", facts?.CountryCode, facts != null ? Ui.DescribeLocation(facts) : "");
        var row = Ui.Columns("auto", "*", "auto");
        row.ColumnSpacing = 10;
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon.InColumn(0));
        var text = new StackPanel();
        text.Children.Add(Ui.Strong(device.Name));
        text.Children.Add(Ui.Secondary(device.Host));
        string detail = facts == null ? "尚未扫描"
            : string.Join(" · ", new[]
            {
                facts.Os,
                facts.CpuCount > 0 ? $"负载 {facts.Load1:0.00}" : "",
                facts.MemoryTotalKb > 0 ? $"内存 {100.0 * (facts.MemoryTotalKb - facts.MemoryAvailableKb) / facts.MemoryTotalKb:0}%" : "",
            }.Where(s => s.Length > 0));
        text.Children.Add(Ui.Secondary(detail));
        row.Children.Add(text.InColumn(1));
        if (App.ProbingDevices.Contains(device.Id)) row.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 }.InColumn(2));

        var primary = PrimaryConnection(device);
        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10),
        };
        ToolTipService.SetToolTip(button, primary != null ? $"打开 {primary.Name}（右键查看全部连接）" : "前往设备页");
        button.Click += (_, _) =>
        {
            if (primary != null) _ = Sessions.OpenConnectionAsync(device, primary);
            else Navigate("devices");
        };
        var menu = new MenuFlyout();
        foreach (var connection in device.Connections.Where(device.Shows))
        {
            var captured = connection;
            menu.Items.Add(Ui.MenuItem(connection.Name, Ui.GlyphFor(connection.Protocol), () => _ = Sessions.OpenConnectionAsync(device, captured)));
        }
        if (DevicesPage.SupportsSsh(device))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.MenuItem("扫描", Ui.GlyphRefresh, () => _ = ProbeQuietlyAsync(device)));
        }
        if (menu.Items.Count > 0) button.ContextFlyout = menu;
        return button;
    }

    private async Task ProbeQuietlyAsync(Device device)
    {
        try
        {
            await App.ProbeAsync(device);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Dialogs.AlertAsync($"无法扫描 {device.Name}", ex.Message);
        }
    }
}

/// <summary>Connections and local shells by when they were last opened.</summary>
internal sealed class RecentWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    public override string DefaultTitle => NameOf(HomeWidgetKind.Recent);
    public override string Glyph => GlyphOf(HomeWidgetKind.Recent);
    public override bool UsesMaxItems => true;

    private sealed record Item(DateTimeOffset Time, string Glyph, string Name, string Detail, Action Open);

    private List<Item> Items()
    {
        var items = new List<Item>();
        foreach (var device in App.Store.Data.Devices)
            foreach (var connection in device.Connections.Where(c => c.LastUsed != null && device.Shows(c)))
            {
                var (d, c) = (device, connection);
                items.Add(new Item(c.LastUsed!.Value, Ui.GlyphFor(c.Protocol), d.Name, c.Name, () => _ = Sessions.OpenConnectionAsync(d, c)));
            }
        foreach (var (key, time) in App.Store.Data.LocalLastUsed)
            if (App.FindLocal(key) is { } target)
                items.Add(new Item(time, Ui.GlyphFor(target.Kind), target.Name, "本机 · " + Ui.Name(target.Kind), () => _ = Sessions.OpenLocalAsync(target)));
        return items.OrderByDescending(i => i.Time).Take(Model.MaxItems).ToList();
    }

    protected override string? Signature() => string.Join("|", Items().Select(i => i.Name + i.Detail + i.Time.ToUnixTimeSeconds() / 60));

    protected override void Build()
    {
        var items = Items();
        if (items.Count == 0)
        {
            Panel.Children.Add(Ui.Secondary("打开过的连接会出现在这里。", true));
            return;
        }
        Panel.Spacing = 0;
        foreach (var item in items)
        {
            var row = Ui.Columns("auto", "*", "auto");
            row.ColumnSpacing = 8;
            var icon = Ui.Icon(item.Glyph, 14);
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon.InColumn(0));
            var text = new StackPanel();
            text.Children.Add(Ui.Body(item.Name));
            text.Children.Add(Ui.Secondary(item.Detail));
            row.Children.Add(text.InColumn(1));
            var time = Ui.Secondary(Ui.RelativeTime(item.Time));
            time.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(time.InColumn(2));
            Panel.Children.Add(RowButton(row, item.Open, "打开"));
        }
    }
}

/// <summary>Load, memory and uptime of each machine from its last scan, with a button to scan them all.</summary>
internal sealed class ResourcesWidget : HomeWidgetView
{
    private readonly Button _scanAll;
    private bool _scanning;

    public ResourcesWidget(HomeWidget model, SessionManager sessions) : base(model, sessions)
    {
        _scanAll = Ui.Button("全部扫描", Ui.GlyphRefresh, () => _ = ScanAllAsync());
    }

    public override string DefaultTitle => Model.Group.Length > 0 ? "资源数据 · " + Model.Group : NameOf(HomeWidgetKind.Resources);
    public override string Glyph => GlyphOf(HomeWidgetKind.Resources);
    public override FrameworkElement? HeaderAction => _scanAll;
    public override bool UsesGroup => true;

    protected override string? Signature() =>
        string.Join("|", FilteredDevices().Select(d => $"{d.Id}{d.Name}{d.Facts?.ScannedAt}{App.ProbingDevices.Contains(d.Id)}")) + _scanning;

    protected override void Build()
    {
        _scanAll.IsEnabled = !_scanning;
        var devices = FilteredDevices().Where(DevicesPage.SupportsSsh).ToList();
        if (devices.Count == 0)
        {
            Panel.Children.Add(Ui.Secondary("没有可扫描的 SSH 设备。", true));
            return;
        }
        var header = Columns();
        string[] names = { "设备", "负载", "内存", "运行时间" };
        for (int i = 0; i < names.Length; i++) header.Children.Add(Ui.Secondary(names[i]).InColumn(i));
        Panel.Children.Add(header);

        foreach (var device in devices)
        {
            var row = Columns();
            var name = new StackPanel();
            name.Children.Add(Ui.Body(device.Name));
            name.Children.Add(Ui.Secondary(App.ProbingDevices.Contains(device.Id) ? "正在扫描…" : device.Facts != null ? "扫描于 " + Ui.RelativeTime(device.Facts.ScannedAt) : "尚未扫描"));
            row.Children.Add(name.InColumn(0));
            if (device.Facts is { } facts)
            {
                double loadPercent = facts.CpuCount > 0 ? 100 * facts.Load1 / facts.CpuCount : 0;
                row.Children.Add(Gauge(loadPercent, $"{facts.Load1:0.00} / {facts.CpuCount} 核").InColumn(1));
                double memory = facts.MemoryTotalKb > 0 ? 100.0 * (facts.MemoryTotalKb - facts.MemoryAvailableKb) / facts.MemoryTotalKb : 0;
                row.Children.Add(Gauge(memory, facts.MemoryTotalKb > 0 ? $"{memory:0}% · {Ui.FormatSize(facts.MemoryTotalKb * 1024)}" : "—").InColumn(2));
                var uptime = Ui.Secondary(facts.UptimeSeconds > 0 ? Ui.FormatDuration(facts.UptimeSeconds) : "—");
                uptime.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(uptime.InColumn(3));
            }
            Panel.Children.Add(row);
        }
    }

    private static Grid Columns()
    {
        var grid = Ui.Columns("4*", "3*", "3*", "90");
        grid.ColumnSpacing = 12;
        return grid;
    }

    private static FrameworkElement Gauge(double percent, string label)
    {
        var panel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Meter(percent));
        panel.Children.Add(Ui.Secondary(label));
        return panel;
    }

    private async Task ScanAllAsync()
    {
        _scanning = true;
        Invalidate();
        Refresh();
        var failed = new List<string>();
        foreach (var device in FilteredDevices().Where(DevicesPage.SupportsSsh).ToList())
        {
            try
            {
                await App.ProbeAsync(device);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add($"{device.Name}：{ex.Message}");
            }
        }
        _scanning = false;
        Invalidate();
        Refresh();
        if (failed.Count > 0) await Dialogs.AlertAsync("部分设备无法扫描", string.Join("\n", failed));
    }
}

/// <summary>CPU and memory of one machine, sampled every few seconds while the home page is open.</summary>
internal sealed class LiveMonitorWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    private readonly MetricChart _chart = new("CPU 与内存", HostMonitor.Capacity, HostMonitor.IntervalSeconds) { Height = 170 };
    private readonly TextBlock[] _values = Enumerable.Range(0, 3).Select(_ => new TextBlock { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }).ToArray();
    private readonly TextBlock _status = Ui.Secondary("", true);
    private HostMonitor? _monitor;
    private bool _loaded;

    private Device? Device => Model.DeviceId is { } id ? App.Store.FindDevice(id) : null;

    public override string DefaultTitle => Device is { } device ? "实时监控 · " + device.Name : NameOf(HomeWidgetKind.LiveMonitor);
    public override string Glyph => GlyphOf(HomeWidgetKind.LiveMonitor);
    public override bool UsesDevice => true;

    protected override string? Signature() => Model.DeviceId?.ToString() ?? "";

    protected override void Build()
    {
        if (Device == null)
        {
            Panel.Children.Add(Ui.Secondary("在编辑模式下点组件设置，选择要监控的设备。", true));
            return;
        }
        var tiles = Ui.Columns("*", "*", "*");
        tiles.ColumnSpacing = 8;
        string[] names = { "CPU", "内存", "负载" };
        for (int i = 0; i < names.Length; i++)
        {
            var tile = new StackPanel();
            tile.Children.Add(Ui.Secondary(names[i]));
            _values[i].Text = "—";
            tile.Children.Add(_values[i]);
            tiles.Children.Add(tile.InColumn(i));
        }
        Panel.Children.Add(tiles);
        Panel.Children.Add(_chart);
        Panel.Children.Add(_status);
        if (_loaded) Restart();
    }

    public override void Loaded()
    {
        _loaded = true;
        if (_monitor == null && Device != null) Restart();
    }

    public override void Unloaded()
    {
        _loaded = false;
        _monitor?.Dispose();
        _monitor = null;
    }

    private void Restart()
    {
        _monitor?.Dispose();
        _monitor = null;
        if (Device is not { } device) return;
        var monitor = new HostMonitor(App.Ssh, device);
        monitor.Updated += () => App.Dispatcher.TryEnqueue(() =>
        {
            if (monitor == _monitor) Update(monitor);
        });
        _monitor = monitor;
        _status.Text = "正在连接…";
        monitor.Start();
    }

    private void Update(HostMonitor monitor)
    {
        var samples = monitor.Samples;
        _status.Text = monitor.Error ?? (samples.Count > 0 ? $"每 {HostMonitor.IntervalSeconds} 秒采样" : "正在连接…");
        if (samples.Count == 0) return;
        var latest = samples[^1];
        _values[0].Text = latest.CpuPercent is { } cpu ? $"{cpu:0}%" : "—";
        _values[1].Text = latest.MemoryPercent is { } memory ? $"{memory:0}%" : "—";
        _values[2].Text = latest.Load1.ToString("0.00");
        _chart.SetData(new[]
        {
            new ChartSeries("CPU", samples.Select(s => s.CpuPercent).ToList()),
            new ChartSeries("内存", samples.Select(s => s.MemoryPercent).ToList()),
        }, v => $"{v:0}%", 100);
    }
}

/// <summary>Port forwarding rules with start and stop.</summary>
internal sealed class ForwardsWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    public override string DefaultTitle => NameOf(HomeWidgetKind.Forwards);
    public override string Glyph => GlyphOf(HomeWidgetKind.Forwards);
    public override bool UsesMaxItems => true;

    protected override string? Signature() =>
        string.Join("|", App.Store.Data.PortForwards.Take(Model.MaxItems).Select(r => $"{r.Id}{r.Name}{App.IsForwardRunning(r)}{(App.Forwards.TryGetValue(r.Id, out var s) ? s.Error : "")}"));

    protected override void Build()
    {
        var rules = App.Store.Data.PortForwards;
        if (rules.Count == 0)
        {
            Panel.Children.Add(Empty("还没有端口转发规则。", "前往端口转发", Ui.GlyphForward, () => Navigate("forwards")));
            return;
        }
        foreach (var rule in rules.Take(Model.MaxItems))
        {
            var row = Ui.Columns("*", "auto");
            row.ColumnSpacing = 8;
            var text = new StackPanel();
            text.Children.Add(Ui.Body(rule.Name.Length > 0 ? rule.Name : $"{rule.BindPort}"));
            string device = App.Store.FindDevice(rule.DeviceId)?.Name ?? "（设备已删除）";
            string route = rule.Kind == PortForwardKind.Dynamic ? $"SOCKS {rule.BindAddress}:{rule.BindPort}" : $"{rule.BindAddress}:{rule.BindPort} → {rule.TargetHost}:{rule.TargetPort}";
            App.Forwards.TryGetValue(rule.Id, out var state);
            string status = state?.IsStarting == true ? "正在连接" : state?.Tunnel != null ? "运行中" : state?.Error is { } error ? "已停止：" + error : "未运行";
            text.Children.Add(Ui.Secondary($"{device} · {route} · {status}"));
            row.Children.Add(text.InColumn(0));
            var toggle = new ToggleSwitch { IsOn = App.IsForwardRunning(rule), OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            var captured = rule;
            toggle.Toggled += (_, _) =>
            {
                if (toggle.IsOn && !App.IsForwardRunning(captured)) _ = App.StartForwardAsync(captured);
                else if (!toggle.IsOn) App.StopForward(captured);
            };
            row.Children.Add(toggle.InColumn(1));
            Panel.Children.Add(row);
        }
    }
}

/// <summary>WSL, Docker and Multipass on this computer.</summary>
internal sealed class EnvironmentWidget : HomeWidgetView
{
    private readonly Button _rescan;

    public EnvironmentWidget(HomeWidget model, SessionManager sessions) : base(model, sessions)
    {
        _rescan = Ui.Button("重新扫描", Ui.GlyphRefresh, () => _ = App.ScanLocalAsync());
    }

    public override string DefaultTitle => NameOf(HomeWidgetKind.Environment);
    public override string Glyph => GlyphOf(HomeWidgetKind.Environment);
    public override FrameworkElement? HeaderAction => _rescan;

    protected override string? Signature() => App.IsScanning + string.Join("|", App.Scan?.Statuses.Select(s => s.Kind + s.Message) ?? Array.Empty<string>());

    protected override void Build()
    {
        _rescan.IsEnabled = !App.IsScanning;
        var grid = new CardGridPanel { MinCardWidth = 200, Spacing = 8 };
        foreach (var kind in new[] { LocalKind.Wsl, LocalKind.Docker, LocalKind.Multipass })
        {
            var status = App.Scan?.Statuses.FirstOrDefault(s => s.Kind == kind);
            var panel = new StackPanel { Spacing = 4 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(Ui.Icon(Ui.GlyphFor(kind)));
            title.Children.Add(Ui.Strong(Ui.Name(kind)));
            panel.Children.Add(title);
            panel.Children.Add(Ui.Secondary(App.IsScanning ? "正在扫描…" : status?.Message ?? "尚未扫描", true));
            grid.Children.Add(Ui.Card(panel, 12));
        }
        Panel.Children.Add(grid);
    }
}

/// <summary>One-click local shells and WSL distributions.</summary>
internal sealed class LocalShellsWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    public override string DefaultTitle => NameOf(HomeWidgetKind.LocalShells);
    public override string Glyph => GlyphOf(HomeWidgetKind.LocalShells);

    private List<LocalTarget> Targets() => App.LocalTargets(LocalKind.Shell).Concat(App.LocalTargets(LocalKind.Wsl)).ToList();

    protected override string? Signature() => string.Join("|", Targets().Select(t => t.StableKey));

    protected override void Build()
    {
        var wrap = new WrapPanel { Spacing = 6 };
        foreach (var target in Targets())
        {
            var captured = target;
            string name = target.Kind == LocalKind.Wsl ? "WSL: " + target.Name : target.Name;
            wrap.Children.Add(Ui.Chip(Ui.GlyphFor(target.Kind), name, "打开 " + name, () => _ = Sessions.OpenLocalAsync(captured)));
        }
        if (wrap.Children.Count == 0) Panel.Children.Add(Ui.Secondary("没有找到本地 Shell。"));
        else Panel.Children.Add(wrap);
    }
}

/// <summary>The built-in Tailscale node and the machines online in the tailnet.</summary>
internal sealed class TailscaleWidget(HomeWidget model, SessionManager sessions) : HomeWidgetView(model, sessions)
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private TailscaleStatus? _status;
    private bool _subscribed;

    public override string DefaultTitle => NameOf(HomeWidgetKind.Tailscale);
    public override string Glyph => GlyphOf(HomeWidgetKind.Tailscale);
    public override bool UsesMaxItems => true;

    private TailscaleNode Node => TailscaleNode.Shared;

    protected override string? Signature() =>
        $"{Node.IsHelperAvailable}{Node.IsStarting}{Node.IsStarted}{Node.LastError}{_status?.BackendState}{_status?.Self?.DnsName}" +
        string.Join("|", _status?.Peers.Take(Model.MaxItems).Select(p => p.HostName + p.Online) ?? Array.Empty<string>());

    protected override void Build()
    {
        if (!Node.IsHelperAvailable)
        {
            Panel.Children.Add(Ui.Secondary("此版本没有包含内置 Tailscale 组件。", true));
            return;
        }
        string state = Node.IsStarting ? "正在启动…"
            : !Node.IsStarted ? "未启动"
            : _status == null ? "已启动"
            : _status.IsRunning ? "已连接" + (_status.Tailnet.Length > 0 ? " · " + _status.Tailnet : "")
            : _status.NeedsLogin ? "需要登录" : _status.BackendState;
        var head = Ui.Columns("*", "auto");
        var text = new StackPanel();
        text.Children.Add(Ui.Body(state));
        if (_status?.Self is { } self) text.Children.Add(Ui.Secondary(string.Join(" · ", new[] { self.DnsName.TrimEnd('.'), self.Addresses.FirstOrDefault() ?? "" }.Where(s => s.Length > 0))));
        else if (!Node.IsStarted && Node.LastError is { } error) text.Children.Add(Ui.Secondary(error, true));
        head.Children.Add(text.InColumn(0));
        var settings = Ui.Button(Node.IsStarted && _status?.NeedsLogin != true ? "设置" : Node.IsStarted ? "去登录" : "启动", Ui.GlyphNetwork, () =>
        {
            if (!Node.IsStarted && !Node.IsStarting) _ = StartAsync();
            else Navigate("settings");
        });
        settings.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(settings.InColumn(1));
        Panel.Children.Add(head);

        if (_status is not { IsRunning: true }) return;
        var online = _status.Peers.Where(p => p.Online).ToList();
        Panel.Children.Add(Ui.Secondary($"在线设备 {online.Count}/{_status.Peers.Count}"));
        foreach (var peer in online.Take(Model.MaxItems))
        {
            var row = Ui.Columns("*", "auto");
            row.Children.Add(Ui.Body(peer.HostName).InColumn(0));
            row.Children.Add(Ui.Secondary(peer.Addresses.FirstOrDefault() ?? peer.BestAddress).InColumn(1));
            Panel.Children.Add(row);
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await Node.StartAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Dialogs.AlertAsync("Tailscale", ex.Message);
        }
        await PollAsync();
    }

    public override void Loaded()
    {
        if (!_subscribed)
        {
            Node.StateChanged += OnNodeChanged;
            _timer.Tick += OnTick;
            _subscribed = true;
        }
        _timer.Start();
        _ = PollAsync();
    }

    public override void Unloaded()
    {
        _timer.Stop();
        if (_subscribed)
        {
            Node.StateChanged -= OnNodeChanged;
            _timer.Tick -= OnTick;
            _subscribed = false;
        }
    }

    private void OnTick(object? sender, object e) => _ = PollAsync();

    private void OnNodeChanged() => App.Dispatcher.TryEnqueue(() => _ = PollAsync());

    private async Task PollAsync()
    {
        if (Node.IsStarted)
        {
            try
            {
                _status = await Node.GetStatusAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                _status = null;
            }
        }
        else
        {
            _status = null;
        }
        Refresh();
    }
}
