using EasyTerminal.Controls;
using EasyTerminal.Core.Local;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Snippets;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Core.Transport;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Sessions;

/// <summary>A tab that shows information about a device rather than a terminal, such as its tmux sessions or live charts.</summary>
public interface ISessionPanel : IDisposable
{
    bool IsLive { get; }
    string StatusText { get; }
    event Action? StateChanged;
    void SetActive(bool active);
    void Restart();
}

/// <summary>One tab: either a terminal session or an SFTP browser, plus how to recreate it.</summary>
public sealed class SessionTab
{
    public required string BaseTitle { get; init; }
    public required string Glyph { get; init; }
    public required FrameworkElement Content { get; init; }
    public Device? Device { get; init; }
    public Connection? Connection { get; init; }
    public LocalTarget? LocalTarget { get; init; }
    public RemoteSession? RemoteSession { get; init; }
    public LocalTarget? RemoteContainer { get; init; }
    public TerminalSession? Terminal { get; init; }
    public TerminalView? View { get; init; }
    public SftpPane? Sftp { get; init; }
    public RdpPane? Rdp { get; init; }
    public WebPane? Web { get; init; }
    public ISessionPanel? Panel { get; init; }
    /// <summary>Opens another tab like this one; null when only the original connection can do that.</summary>
    public Action? Reopen { get; init; }
    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.Now;

    public bool IsLocal => LocalTarget != null;
    public bool IsLive => Panel != null ? Panel.IsLive : Rdp != null ? Rdp.IsConnected : Web != null || (Sftp != null ? Sftp.IsConnected : Terminal?.State is SessionState.Starting or SessionState.Running);

    /// <summary>Closing a list, a chart or a web page loses nothing, so only real sessions ask first.</summary>
    public bool HasForegroundWork => Panel == null && Web == null && IsLive;

    public string Title
    {
        get
        {
            string live = Terminal?.Buffer.Title ?? "";
            string title = live.Length > 0 && live.Length < 48 ? live : BaseTitle;
            return Terminal?.State is SessionState.Exited or SessionState.Failed ? title + "（已结束）" : title;
        }
    }

    public string StatusText => Terminal == null
        ? (Panel != null ? Panel.StatusText : Web != null ? "网页" : IsLive ? "已连接" : "未连接")
        : Terminal.State switch
        {
            SessionState.Starting => "正在启动",
            SessionState.Running => Terminal.ProcessId is { } pid ? $"运行中 · PID {pid}" : "运行中",
            SessionState.Exited => $"已退出（代码 {Terminal.ExitCode?.ToString() ?? "未知"}）",
            _ => "启动失败",
        };

    public void SetActive(bool active)
    {
        Content.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        View?.SetActive(active);
        Rdp?.SetActive(active);
        Panel?.SetActive(active);
        if (active) View?.FocusTerminal();
    }

    public void Restart()
    {
        if (Terminal != null) Terminal.Restart();
        else if (Panel != null) Panel.Restart();
        else if (Rdp != null) Rdp.Reconnect();
        else if (Web != null) Web.Reload();
        else Sftp?.Reconnect();
    }

    public void Dispose()
    {
        View?.Dispose();
        Terminal?.Dispose();
        Sftp?.Dispose();
        Rdp?.Dispose();
        Web?.Dispose();
        Panel?.Dispose();
    }
}

public sealed class SessionManager
{
    private readonly AppServices _app;
    private readonly List<SessionTab> _tabs = new();

    public IReadOnlyList<SessionTab> Tabs => _tabs;
    public event Action<SessionTab>? Added;
    public event Action<SessionTab>? Removed;
    public event Action<SessionTab>? Changed;
    public event Action? CommandPaletteRequested;
    public event Action<string>? AskAssistantRequested;
    public event Action? BroadcastChanged;

    /// <summary>While on, what is typed into one terminal also goes to every other running terminal.</summary>
    public bool IsBroadcasting { get; private set; }

    public void SetBroadcasting(bool on)
    {
        if (IsBroadcasting == on) return;
        IsBroadcasting = on;
        BroadcastChanged?.Invoke();
    }

    public SessionManager(AppServices app)
    {
        _app = app;
        _app.SettingsChanged += () =>
        {
            foreach (var tab in _tabs) tab.View?.SetAppearance(_app.Appearance);
        };
    }

    // ---- Opening -----------------------------------------------------------------------------

    public async Task OpenLocalAsync(LocalTarget target)
    {
        if (target.Kind != LocalKind.Shell && target.Kind != LocalKind.Wsl && !target.IsRunning)
        {
            if (!await Dialogs.ConfirmAsync($"{target.Name} 没有运行", $"要先启动这个 {Ui.Name(target.Kind)} 环境再连接吗？", "启动并连接")) return;
            try
            {
                await LocalScanner.StartTargetAsync(target);
            }
            catch (Exception ex)
            {
                await Dialogs.AlertAsync($"无法启动 {target.Name}", ex.Message);
                return;
            }
            _ = _app.ScanLocalAsync();
        }

        string commandLine = CommandLine.Join(target.Command);
        string? workingDirectory = target.Kind == LocalKind.Shell ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null;
        var tab = AddTerminal(target.Name, Ui.GlyphFor(target.Kind), () => new ConPtyTransport(commandLine, workingDirectory), localTarget: target);
        _app.Store.Data.LocalLastUsed[target.StableKey] = DateTimeOffset.Now;
        _app.Store.Save();
        _ = tab;
    }

    public async Task OpenConnectionAsync(Device device, Connection connection)
    {
        connection.LastUsed = DateTimeOffset.Now;
        _app.Store.Save();

        switch (connection.Protocol)
        {
            case ConnectionProtocol.Sftp:
                OpenSftp(device, connection);
                break;
            case ConnectionProtocol.Mosh:
                AddTerminal(connection.Name, Ui.GlyphFor(connection.Protocol), () => _app.CreateMoshTransport(device, connection), device, connection);
                break;
            case ConnectionProtocol.Tmux when connection.SessionName.Length == 0:
                OpenPanel(device, connection, new ResourcePane(this, device, ResourceKind.Tmux));
                break;
            case ConnectionProtocol.Docker:
                OpenPanel(device, connection, new ResourcePane(this, device, ResourceKind.Docker));
                break;
            case ConnectionProtocol.Proxmox:
                OpenPanel(device, connection, new ResourcePane(this, device, ResourceKind.Proxmox));
                break;
            // Tools and network were tabs of their own in 1.0.5; they are now part of the status tab.
            case ConnectionProtocol.Monitor or ConnectionProtocol.Tools or ConnectionProtocol.Network:
                OpenPanel(device, connection, new MonitorPane(this, device));
                break;
            case ConnectionProtocol.Tmux or ConnectionProtocol.Herdr:
                var kind = connection.Protocol == ConnectionProtocol.Tmux ? RemoteSessionKind.Tmux : RemoteSessionKind.Herdr;
                string attach = RemoteProbe.AttachCommand(kind, connection.SessionName);
                AddTerminal(connection.Name, Ui.GlyphFor(connection.Protocol), () => _app.CreateSshTransport(device, connection, attach), device, connection);
                break;
            case ConnectionProtocol.Rdp:
                OpenRdp(device, connection);
                break;
            case ConnectionProtocol.Telnet:
                AddTerminal(connection.Name, Ui.GlyphFor(connection.Protocol), () => _app.CreateTelnetTransport(device, connection), device, connection);
                break;
            case ConnectionProtocol.Serial:
                AddTerminal(connection.Name, Ui.GlyphFor(connection.Protocol), () => new SerialTransport(device.Host, connection.BaudRate), device, connection);
                break;
            default:
                string? command = string.IsNullOrWhiteSpace(connection.Command) ? null : connection.Command;
                if (command == null && _app.Store.Data.Snippets.FirstOrDefault(s => s.Id == connection.StartupSnippetId) is { } startup)
                    command = SnippetTemplate.StartupCommand(startup.Body);
                AddTerminal(connection.Name, Ui.GlyphFor(connection.Protocol), () => _app.CreateSshTransport(device, connection, command), device, connection);
                break;
        }
        await Task.CompletedTask;
    }

    public Task OpenRemoteSessionAsync(Device device, RemoteSession session)
    {
        string attach = RemoteProbe.AttachCommand(session.Kind, session.Name);
        AddTerminal(session.Name, Ui.GlyphSessions, () => _app.CreateSshTransport(device, null, attach), device, remoteSession: session);
        return Task.CompletedTask;
    }

    /// <summary>Opens a terminal running one command on a device, such as attaching to a tmux window or following logs.</summary>
    public void OpenRemoteCommand(Device device, string title, string glyph, string command)
    {
        AddTerminal(title, glyph, () => _app.CreateSshTransport(device, null, command), device, reopen: () => OpenRemoteCommand(device, title, glyph, command));
    }

    public void OpenWeb(string title, string url, Device? device)
    {
        var pane = new WebPane(url);
        var tab = new SessionTab { BaseTitle = title, Glyph = Ui.GlyphGlobe, Content = pane, Device = device, Web = pane, Reopen = () => OpenWeb(title, url, device) };
        _tabs.Add(tab);
        Added?.Invoke(tab);
    }

    private void OpenPanel(Device device, Connection connection, ISessionPanel panel)
    {
        var tab = new SessionTab { BaseTitle = connection.Name, Glyph = Ui.GlyphFor(connection.Protocol), Content = (FrameworkElement)panel, Device = device, Connection = connection, Panel = panel };
        panel.StateChanged += () => Changed?.Invoke(tab);
        _tabs.Add(tab);
        Added?.Invoke(tab);
        panel.Restart();
    }

    private void OpenRdp(Device device, Connection connection)
    {
        var pane = new RdpPane(device, connection);
        var tab = new SessionTab { BaseTitle = connection.Name, Glyph = Ui.GlyphRemoteDesktop, Content = pane, Device = device, Connection = connection, Rdp = pane };
        pane.StateChanged += () => Changed?.Invoke(tab);
        _tabs.Add(tab);
        Added?.Invoke(tab);
        pane.Reconnect();
    }

    public async Task OpenRemoteContainerAsync(Device device, LocalTarget container, string user = "")
    {
        if (!container.IsRunning)
        {
            if (!await Dialogs.ConfirmAsync($"{container.Name} 没有运行", $"要先在 {device.Name} 上启动这个容器再连接吗？", "启动并连接")) return;
            try
            {
                await _app.RunContainerActionAsync(device, container, "start");
            }
            catch (Exception ex)
            {
                await Dialogs.AlertAsync($"无法启动 {container.Name}", ex.Message);
                return;
            }
        }
        bool sudo = _app.Probes.TryGetValue(device.Id, out var probe) && probe.DockerNeedsSudo;
        string exec = RemoteProbe.DockerExecCommand(container.Id, sudo, user);
        string title = user.Length > 0 ? $"{container.Name}（{user}）" : container.Name;
        AddTerminal(title, Ui.GlyphContainer, () => _app.CreateSshTransport(device, null, exec), device, remoteContainer: container);
    }

    /// <summary>Opens a shell in a Proxmox guest, offering to start it first when it is stopped.</summary>
    public async Task OpenGuestAsync(Device device, PveGuest guest)
    {
        bool sudo = _app.Probes.TryGetValue(device.Id, out var probe) && probe.ProxmoxNeedsSudo;
        if (!guest.IsRunning)
        {
            if (!await Dialogs.ConfirmAsync($"{guest.Name} 没有运行", $"要先在 {device.Name} 上启动它再连接吗？", "启动并连接")) return;
            try
            {
                await _app.RunRemoteActionAsync(device, ProxmoxCommands.Action(guest, "start", sudo), TimeSpan.FromMinutes(3));
            }
            catch (Exception ex)
            {
                await Dialogs.AlertAsync($"无法启动 {guest.Name}", ex.Message);
                return;
            }
        }
        string command = ProxmoxCommands.Enter(guest, sudo);
        OpenRemoteCommand(device, guest.Name, guest.Kind == PveGuestKind.Lxc ? Ui.GlyphContainer : Ui.GlyphVm, command);
    }

    /// <summary>Connects to user@host:port, saving it as a device the first time.</summary>
    public void QuickConnect(string user, string host, int port)
    {
        var device = _app.Store.Data.Devices.FirstOrDefault(d => d.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && d.Port == port && d.User == user);
        if (device == null)
        {
            device = new Device { Name = host, Host = host, Port = port, User = user };
            _app.Store.Data.Devices.Add(device);
        }
        var connection = device.Connections.FirstOrDefault(c => c.Protocol == ConnectionProtocol.Ssh);
        if (connection == null)
        {
            connection = new Connection { Name = "SSH", Protocol = ConnectionProtocol.Ssh };
            device.Connections.Add(connection);
            device.OfferBuiltInSessions();
        }
        _app.NotifyDevicesChanged();
        _ = OpenConnectionAsync(device, connection);
    }

    private void OpenSftp(Device device, Connection connection)
    {
        var pane = new SftpPane(device, connection);
        var tab = new SessionTab { BaseTitle = connection.Name, Glyph = Ui.GlyphTransfer, Content = pane, Device = device, Connection = connection, Sftp = pane };
        pane.StateChanged += () => Changed?.Invoke(tab);
        _tabs.Add(tab);
        Added?.Invoke(tab);
        pane.Reconnect();
    }

    private SessionTab AddTerminal(string title, string glyph, Func<ITerminalTransport> transport, Device? device = null, Connection? connection = null, LocalTarget? localTarget = null, RemoteSession? remoteSession = null, LocalTarget? remoteContainer = null, Action? reopen = null)
    {
        var session = new TerminalSession(transport, _app.Store.Data.Settings.Scrollback);
        if (_app.Store.Data.Settings.LogSessions)
        {
            try
            {
                session.StartLog(_app.NewSessionLogPath(title));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A session is worth more than its log; the terminal still opens.
            }
        }
        var view = new TerminalView();
        view.SetAppearance(_app.Appearance);
        view.Attach(session);
        var pane = new TerminalPane(session, view);
        var tab = new SessionTab
        {
            BaseTitle = title,
            Glyph = glyph,
            Content = pane,
            Device = device,
            Connection = connection,
            LocalTarget = localTarget,
            RemoteSession = remoteSession,
            RemoteContainer = remoteContainer,
            Terminal = session,
            View = view,
            Reopen = reopen,
        };

        view.FindRequested += (_, _) => pane.ShowFind();
        view.InputSent += bytes =>
        {
            if (!IsBroadcasting) return;
            foreach (var other in _tabs)
                if (other != tab && other.Terminal?.State == SessionState.Running) other.Terminal.Write(bytes);
        };
        view.ExtendContextMenu = menu => AddTerminalMenuItems(menu, tab, pane);
        view.CommandPaletteRequested += (_, _) => CommandPaletteRequested?.Invoke();
        view.FontSizeChangeRequested += (_, _) =>
        {
            var settings = _app.Store.Data.Settings;
            settings.FontSize = Math.Clamp(settings.FontSize + view.FontSizeDelta, 8, 32);
            _app.NotifySettingsChanged();
        };
        session.StateChanged += () => _app.Dispatcher.TryEnqueue(() => Changed?.Invoke(tab));
        session.Buffer.TitleChanged += () => _app.Dispatcher.TryEnqueue(() => Changed?.Invoke(tab));

        _tabs.Add(tab);
        Added?.Invoke(tab);
        return tab;
    }

    private void AddTerminalMenuItems(MenuFlyout menu, SessionTab tab, TerminalPane pane)
    {
        var find = new MenuFlyoutItem { Text = "查找", KeyboardAcceleratorTextOverride = "Ctrl+Shift+F", Icon = Ui.Icon(Ui.GlyphSearch) };
        find.Click += (_, _) => pane.ShowFind();
        menu.Items.Add(find);

        string selected = tab.View?.SelectedText ?? "";
        var ask = Ui.MenuItem("询问 AI 助手", Ui.GlyphAssistant, () => AskAssistantRequested?.Invoke(selected));
        ask.IsEnabled = selected.Length > 0;
        menu.Items.Add(ask);

        var snippets = _app.Store.Data.Snippets.OrderByDescending(s => s.LastUsed).ThenBy(s => s.Name).ToList();
        if (snippets.Count == 0) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        var submenu = new MenuFlyoutSubItem { Text = "运行片段", Icon = Ui.Icon(Ui.GlyphSnippet) };
        foreach (var snippet in snippets.Take(20))
            submenu.Items.Add(Ui.MenuItem(snippet.Name, Ui.GlyphSnippet, () => _ = SnippetRunner.SendAsync(snippet, new[] { tab })));
        menu.Items.Add(submenu);
    }

    public void Duplicate(SessionTab tab)
    {
        if (tab.Reopen != null) tab.Reopen();
        else if (tab.LocalTarget != null) _ = OpenLocalAsync(tab.LocalTarget);
        else if (tab.Device != null && tab.Connection != null) _ = OpenConnectionAsync(tab.Device, tab.Connection);
        else if (tab.Device != null && tab.RemoteSession != null) _ = OpenRemoteSessionAsync(tab.Device, tab.RemoteSession);
        else if (tab.Device != null && tab.RemoteContainer != null) _ = OpenRemoteContainerAsync(tab.Device, tab.RemoteContainer);
    }

    // ---- Closing and persistence -------------------------------------------------------------

    public void Close(SessionTab tab)
    {
        if (!_tabs.Remove(tab)) return;
        Removed?.Invoke(tab);
        tab.Dispose();
    }

    public void CloseAll()
    {
        foreach (var tab in _tabs.ToList()) Close(tab);
    }

    public void SaveOpenTabs()
    {
        var refs = new List<SessionRef>();
        foreach (var tab in _tabs)
        {
            if (tab.LocalTarget != null) refs.Add(new SessionRef { LocalKey = tab.LocalTarget.StableKey });
            else if (tab.Connection != null) refs.Add(new SessionRef { ConnectionId = tab.Connection.Id });
        }
        _app.Store.Data.OpenSessions = refs;
        _app.Store.Save();
    }

    public void RestoreSavedTabs()
    {
        foreach (var saved in _app.Store.Data.OpenSessions.ToList())
        {
            if (saved.LocalKey != null && _app.FindLocal(saved.LocalKey) is { } target)
            {
                // Restoring must never start a stopped container or VM behind the user's back.
                if (target.Kind == LocalKind.Shell || target.IsRunning) _ = OpenLocalAsync(target);
            }
            else if (saved.ConnectionId is { } id && _app.Store.FindConnection(id) is { } found)
            {
                _ = OpenConnectionAsync(found.Device, found.Connection);
            }
        }
    }
}

/// <summary>Hosts a terminal and shows what happened when its session ends.</summary>
public sealed class TerminalPane : Grid
{
    private readonly TerminalSession _session;
    private readonly TerminalView _view;
    private readonly InfoBar _status = new() { IsClosable = true, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12) };
    private readonly TextBox _findBox = new() { PlaceholderText = "查找", Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _findStatus = Ui.Secondary("");
    private readonly Border _findBar;
    private bool _started;

    public TerminalPane(TerminalSession session, TerminalView view)
    {
        _session = session;
        _view = view;
        Children.Add(view);
        Children.Add(_status);

        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        findRow.Children.Add(_findBox);
        _findStatus.VerticalAlignment = VerticalAlignment.Center;
        findRow.Children.Add(_findStatus);
        findRow.Children.Add(Ui.IconButton(Ui.GlyphUp, "上一个（Shift+Enter）", () => Find(true)));
        findRow.Children.Add(Ui.IconButton("", "下一个（Enter）", () => Find(false)));
        findRow.Children.Add(Ui.IconButton(Ui.GlyphClose, "关闭（Esc）", HideFind));
        _findBar = Ui.Card(findRow, 6);
        _findBar.HorizontalAlignment = HorizontalAlignment.Right;
        _findBar.VerticalAlignment = VerticalAlignment.Top;
        _findBar.Margin = new Thickness(0, 8, 24, 0);
        _findBar.Background = Ui.Brush("SolidBackgroundFillColorBaseBrush");
        _findBar.Visibility = Visibility.Collapsed;
        Children.Add(_findBar);
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                Find(shift);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                HideFind();
                e.Handled = true;
            }
        };

        var reconnect = new Button { Content = "重新连接" };
        reconnect.Click += (_, _) =>
        {
            _status.IsOpen = false;
            _session.Restart();
            _view.FocusTerminal();
        };
        _status.ActionButton = reconnect;

        session.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateStatus);
        view.Loaded += OnViewLoaded;
    }

    public void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;
        if (_view.SelectedText is { Length: > 0 and < 200 } selected && !selected.Contains('\n')) _findBox.Text = selected;
        _findBox.Focus(FocusState.Programmatic);
        _findBox.SelectAll();
    }

    private void HideFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _findStatus.Text = "";
        _view.FocusTerminal();
    }

    private void Find(bool backward)
    {
        if (_findBox.Text.Length == 0) return;
        _findStatus.Text = _view.Find(_findBox.Text, backward) ? "" : "未找到";
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        // Wait for layout so the process starts at its real size instead of redrawing right after launch.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var (cols, rows) = _view.MeasureGrid();
            _session.Start(cols, rows);
            _view.FocusTerminal();
        });
    }

    private void UpdateStatus()
    {
        switch (_session.State)
        {
            case SessionState.Exited:
                _status.Title = _session.ExitCode == 0 ? "会话已结束" : $"会话已结束（退出代码 {_session.ExitCode?.ToString() ?? "未知"}）";
                _status.Message = "";
                _status.Severity = _session.ExitCode == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
                _status.IsOpen = true;
                break;
            case SessionState.Failed:
                _status.Title = "无法启动会话";
                _status.Message = _session.Error ?? "";
                _status.Severity = InfoBarSeverity.Error;
                _status.IsOpen = true;
                break;
            default:
                _status.IsOpen = false;
                break;
        }
    }
}
