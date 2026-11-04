using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Sessions;

public enum ResourceKind { Tmux, Docker, Proxmox }

/// <summary>A tab listing what runs on a device, and the way in: tmux sessions, Docker containers or Proxmox guests.</summary>
public sealed class ResourcePane : UserControl, ISessionPanel
{
    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly Device _device;
    private readonly ResourceKind _kind;
    private readonly Button _scan;
    private readonly StackPanel _body = new() { Spacing = 10 };
    private string? _scanError;

    public event Action? StateChanged;

    public bool IsLive => _app.Probes.ContainsKey(_device.Id);
    public string StatusText => _app.ProbingDevices.Contains(_device.Id) ? "正在扫描" : IsLive ? "已扫描" : "未扫描";

    public ResourcePane(SessionManager sessions, Device device, ResourceKind kind)
    {
        _sessions = sessions;
        _device = device;
        _kind = kind;
        var (root, content) = Ui.Page();
        Content = root;

        var header = Ui.Columns("*", "auto", "auto");
        header.Children.Add(Ui.Title(kind switch { ResourceKind.Tmux => "tmux 会话", ResourceKind.Docker => "Docker 容器", _ => "Proxmox 虚拟机和 LXC" }).InColumn(0));
        _scan = Ui.Button("重新扫描", Ui.GlyphRefresh, () => _ = ScanAsync());
        header.Children.Add(_scan.InColumn(1));
        if (kind == ResourceKind.Tmux) header.Children.Add(Ui.Button("新建会话", Ui.GlyphAdd, () => _ = NewSessionAsync(), accent: true).InColumn(2));
        content.Children.Add(header);
        content.Children.Add(_body);

        _app.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged()
    {
        Refresh();
        StateChanged?.Invoke();
    }

    public void SetActive(bool active)
    {
        if (active) Refresh();
    }

    /// <summary>Shows what the last scan found right away and rescans, since an old list could offer sessions that are gone.</summary>
    public void Restart()
    {
        Refresh();
        if (!_app.ProbingDevices.Contains(_device.Id)) _ = ScanAsync();
    }

    private void Refresh()
    {
        _body.Children.Clear();
        bool scanning = _app.ProbingDevices.Contains(_device.Id);
        _scan.IsEnabled = !scanning;
        if (scanning) _body.Children.Add(new ProgressBar { IsIndeterminate = true });
        if (_scanError != null)
        {
            _body.Children.Add(new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = true, IsClosable = false, Title = "无法扫描，下面是上次的结果", Message = _scanError });
        }
        if (!_app.Probes.TryGetValue(_device.Id, out var probe))
        {
            if (!scanning && _scanError == null) _body.Children.Add(Ui.Secondary("还没有扫描结果。"));
            return;
        }
        switch (_kind)
        {
            case ResourceKind.Tmux:
                BuildTmux(probe);
                break;
            case ResourceKind.Docker:
                BuildDocker(probe);
                break;
            default:
                BuildProxmox(probe);
                break;
        }
    }

    private async Task ScanAsync()
    {
        _scanError = null;
        try
        {
            await _app.ProbeAsync(_device);
        }
        catch (Exception ex)
        {
            _scanError = ex.Message;
            Refresh();
        }
    }

    private async Task RunAsync(string title, string command, TimeSpan? timeout = null)
    {
        try
        {
            await _app.RunRemoteActionAsync(_device, command, timeout);
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync(title, ex.Message);
        }
    }

    // ---- tmux --------------------------------------------------------------------------------

    private void BuildTmux(ProbeResult probe)
    {
        if (!probe.HasTmux && !probe.Sessions.Any(s => s.Kind == RemoteSessionKind.Herdr))
        {
            _body.Children.Add(Ui.Secondary("这台设备没有安装 tmux。"));
            return;
        }
        if (probe.Sessions.Count == 0) _body.Children.Add(Ui.Secondary("没有运行中的会话。"));
        foreach (var session in probe.Sessions)
            _body.Children.Add(session.Kind == RemoteSessionKind.Tmux ? BuildTmuxSession(session, probe) : BuildHerdrSession(session));
    }

    private FrameworkElement BuildHerdrSession(RemoteSession session)
    {
        var row = Ui.Columns("auto", "*", "auto", "auto");
        row.Children.Add(Ui.Icon(Ui.GlyphSessions).InColumn(0));
        var text = new StackPanel();
        text.Children.Add(Ui.Strong(session.Name));
        text.Children.Add(Ui.Secondary("herdr · " + session.Detail));
        row.Children.Add(text.InColumn(1));
        row.Children.Add(Ui.Button("Attach", Ui.GlyphPlay, () => _ = _sessions.OpenRemoteSessionAsync(_device, session), accent: true).InColumn(2));
        row.Children.Add(Ui.IconButton(Ui.GlyphStop, "结束会话", () => _ = KillSessionAsync(session)).InColumn(3));
        return Ui.Card(row, 12);
    }

    private FrameworkElement BuildTmuxSession(RemoteSession session, ProbeResult probe)
    {
        var panel = new StackPanel { Spacing = 6 };
        var header = Ui.Columns("auto", "*", "auto", "auto");
        header.Children.Add(Ui.Icon(Ui.GlyphSessions).InColumn(0));
        var text = new StackPanel();
        text.Children.Add(Ui.Strong(session.Name));
        string created = session.Created is { } time ? " · 创建于 " + Ui.RelativeTime(time) : "";
        text.Children.Add(Ui.Secondary(session.Detail + created));
        header.Children.Add(text.InColumn(1));
        header.Children.Add(Ui.Button("Attach", Ui.GlyphPlay, () => Attach(session.Name, TmuxCommands.Attach(session.Name)), accent: true).InColumn(2));
        header.Children.Add(Ui.MenuButton(Ui.GlyphMore, "更多操作", menu =>
        {
            menu.Items.Add(Ui.MenuItem("只读 Attach", Ui.GlyphPlay, () => Attach(session.Name + "（只读）", TmuxCommands.Attach(session.Name, readOnly: true))));
            menu.Items.Add(Ui.MenuItem("踢掉其他客户端后 Attach", Ui.GlyphPlay, () => Attach(session.Name, TmuxCommands.Attach(session.Name, detachOthers: true))));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.MenuItem("新建窗口", Ui.GlyphAdd, () => _ = NewWindowAsync(session.Name)));
            menu.Items.Add(Ui.MenuItem("重命名会话", Ui.GlyphRename, () => _ = RenameSessionAsync(session.Name)));
            var detach = Ui.MenuItem("断开所有客户端", Ui.GlyphClose, () => _ = RunAsync("无法断开客户端", TmuxCommands.DetachClients(session.Name)));
            detach.IsEnabled = session.IsActive;
            menu.Items.Add(detach);
            menu.Items.Add(Ui.MenuItem("保存为连接", Ui.GlyphStar, () => SaveSession(session)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.MenuItem("结束会话", Ui.GlyphStop, () => _ = KillSessionAsync(session)));
        }).InColumn(3));
        panel.Children.Add(header);

        foreach (var window in probe.Windows.Where(w => w.Session == session.Name))
        {
            var row = Ui.Columns("28", "*", "auto", "auto");
            row.Padding = new Thickness(24, 0, 0, 0);
            var index = Ui.Secondary(window.Index.ToString());
            index.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(index.InColumn(0));
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(Ui.Body(window.Name));
            var detail = Ui.Secondary($"{window.Command} · {window.Panes} 个窗格" + (window.IsActive ? " · 当前" : ""));
            detail.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(detail);
            row.Children.Add(line.InColumn(1));
            row.Children.Add(Ui.IconButton(Ui.GlyphPlay, "Attach 到此窗口", () => Attach($"{session.Name}:{window.Name}", TmuxCommands.AttachWindow(session.Name, window.Index))).InColumn(2));
            row.Children.Add(Ui.MenuButton(Ui.GlyphMore, "窗口操作", menu =>
            {
                menu.Items.Add(Ui.MenuItem("重命名窗口", Ui.GlyphRename, () => _ = RenameWindowAsync(window)));
                menu.Items.Add(Ui.MenuItem("关闭窗口", Ui.GlyphClose, () => _ = KillWindowAsync(window)));
            }).InColumn(3));
            panel.Children.Add(row);
        }
        return Ui.Card(panel, 12);
    }

    private void Attach(string title, string command) => _sessions.OpenRemoteCommand(_device, title, Ui.GlyphSessions, command);

    private void SaveSession(RemoteSession session)
    {
        _device.Connections.Add(new Connection { Name = session.Name, Protocol = ConnectionProtocol.Tmux, SessionName = session.Name });
        _app.NotifyDevicesChanged();
    }

    private async Task NewSessionAsync()
    {
        if (await Dialogs.PromptTextAsync("新建 tmux 会话", "会话名称", confirmText: "创建", placeholder: "不能包含 : 和 .") is not { } name) return;
        if (!TmuxCommands.IsValidSessionName(name))
        {
            await Dialogs.AlertAsync("名称无效", "tmux 会话名称不能为空，也不能包含 : 和 .。");
            return;
        }
        await RunAsync("无法创建会话", TmuxCommands.NewSession(name));
    }

    private async Task NewWindowAsync(string session)
    {
        if (await Dialogs.PromptTextAsync("新建窗口", "窗口名称（可选）", confirmText: "创建") is { } name)
            await RunAsync("无法创建窗口", TmuxCommands.NewWindow(session, name));
    }

    private async Task RenameSessionAsync(string session)
    {
        if (await Dialogs.PromptTextAsync("重命名会话", "新名称", session, "重命名") is not { } name || name == session) return;
        if (!TmuxCommands.IsValidSessionName(name))
        {
            await Dialogs.AlertAsync("名称无效", "tmux 会话名称不能为空，也不能包含 : 和 .。");
            return;
        }
        await RunAsync("无法重命名", TmuxCommands.RenameSession(session, name));
    }

    private async Task RenameWindowAsync(TmuxWindow window)
    {
        if (await Dialogs.PromptTextAsync("重命名窗口", "新名称", window.Name, "重命名") is { Length: > 0 } name && name != window.Name)
            await RunAsync("无法重命名", TmuxCommands.RenameWindow(window.Session, window.Index, name));
    }

    private async Task KillWindowAsync(TmuxWindow window)
    {
        if (await Dialogs.ConfirmAsync($"关闭窗口 {window.Name}？", "窗口中运行的程序会被终止。", "关闭", destructive: true))
            await RunAsync("无法关闭窗口", TmuxCommands.KillWindow(window.Session, window.Index));
    }

    private async Task KillSessionAsync(RemoteSession session)
    {
        if (!await Dialogs.ConfirmAsync($"结束会话 {session.Name}？", "会话中运行的程序会被终止。", "结束", destructive: true)) return;
        try
        {
            await _app.StopRemoteSessionAsync(_device, session);
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync("无法结束会话", ex.Message);
        }
    }

    // ---- Docker ------------------------------------------------------------------------------

    private void BuildDocker(ProbeResult probe)
    {
        if (!probe.HasDocker)
        {
            _body.Children.Add(Ui.Secondary("这台设备没有安装 Docker。"));
            return;
        }
        if (probe.DockerError != null) _body.Children.Add(Ui.Secondary(probe.DockerError));
        else if (probe.Containers.Count == 0) _body.Children.Add(Ui.Secondary("没有容器。"));
        foreach (var container in probe.Containers) _body.Children.Add(BuildContainer(container, probe));
    }

    private FrameworkElement BuildContainer(LocalTarget container, ProbeResult probe)
    {
        var row = Ui.Columns("auto", "*", "auto", "auto");
        row.Children.Add(Ui.Icon(Ui.GlyphContainer).InColumn(0));
        var text = new StackPanel();
        text.Children.Add(Ui.Strong(container.Name));
        text.Children.Add(Ui.Secondary(container.Detail));
        row.Children.Add(text.InColumn(1));
        row.Children.Add(Ui.Button("进入", Ui.GlyphPlay, () => _ = _sessions.OpenRemoteContainerAsync(_device, container), accent: true).InColumn(2));
        row.Children.Add(Ui.MenuButton(Ui.GlyphMore, "更多操作", menu =>
        {
            menu.Items.Add(Ui.MenuItem("以 root 进入", Ui.GlyphPlay, () => _ = _sessions.OpenRemoteContainerAsync(_device, container, "root")));
            menu.Items.Add(Ui.MenuItem("查看日志", Ui.GlyphLog, () => _sessions.OpenRemoteCommand(_device, container.Name + " 日志", Ui.GlyphLog, RemoteProbe.DockerLogsCommand(container.Id, probe.DockerNeedsSudo))));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (container.IsRunning)
            {
                menu.Items.Add(Ui.MenuItem("重启", Ui.GlyphRefresh, () => _ = ContainerActionAsync(container, "restart")));
                menu.Items.Add(Ui.MenuItem("停止", Ui.GlyphStop, () => _ = ContainerActionAsync(container, "stop")));
            }
            else
            {
                menu.Items.Add(Ui.MenuItem("启动", Ui.GlyphPlay, () => _ = ContainerActionAsync(container, "start")));
            }
        }).InColumn(3));
        return Ui.Card(row, 12);
    }

    private async Task ContainerActionAsync(LocalTarget container, string action)
    {
        if (action is "stop" or "restart" && !await Dialogs.ConfirmAsync($"{(action == "stop" ? "停止" : "重启")} {container.Name}？", "容器内运行的进程会结束。", action == "stop" ? "停止" : "重启", destructive: true)) return;
        try
        {
            await _app.RunContainerActionAsync(_device, container, action);
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync("操作失败", ex.Message);
        }
    }

    // ---- Proxmox VE --------------------------------------------------------------------------

    private void BuildProxmox(ProbeResult probe)
    {
        if (!probe.HasProxmox)
        {
            _body.Children.Add(Ui.Secondary("这台设备不是 Proxmox 节点。"));
            return;
        }
        if (probe.ProxmoxError != null) _body.Children.Add(Ui.Secondary(probe.ProxmoxError, true));
        else if (probe.Guests.Count == 0) _body.Children.Add(Ui.Secondary("这个节点上没有虚拟机或容器。"));
        foreach (var guest in probe.Guests) _body.Children.Add(BuildGuest(guest));
    }

    private FrameworkElement BuildGuest(PveGuest guest)
    {
        bool isLxc = guest.Kind == PveGuestKind.Lxc;
        var row = Ui.Columns("auto", "*", "auto", "auto");
        row.Children.Add(Ui.Icon(GuestActions.Glyph(guest)).InColumn(0));
        var text = new StackPanel();
        text.Children.Add(Ui.Strong($"{guest.Id} · {guest.Name}"));
        text.Children.Add(Ui.Secondary($"{guest.Detail} · {GuestActions.DescribeStatus(guest)}"));
        row.Children.Add(text.InColumn(1));

        var enter = Ui.Button(isLxc ? "进入" : "网页控制台", isLxc ? Ui.GlyphPlay : Ui.GlyphGlobe, () => GuestActions.Enter(_sessions, _device, guest), accent: true);
        row.Children.Add(enter.InColumn(2));
        row.Children.Add(Ui.MenuButton(Ui.GlyphMore, "更多操作", menu => GuestActions.AddMenuItems(menu.Items, _sessions, _device, guest)).InColumn(3));
        return Ui.Card(row, 12);
    }

    public void Dispose() => _app.DevicesChanged -= OnDevicesChanged;
}
