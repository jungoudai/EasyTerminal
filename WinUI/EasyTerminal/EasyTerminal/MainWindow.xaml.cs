using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Pages;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace EasyTerminal;

public sealed partial class MainWindow : Window
{
    private const string LocalFolderKey = "local";

    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly Dictionary<SessionTab, (NavigationViewItem Item, TextBlock Title)> _tabItems = new();
    private readonly HashSet<string> _collapsedFolders = new();
    private readonly NavigationViewItem _homeItem = new() { Content = "主页", Tag = "home", Icon = Ui.Icon(Ui.GlyphHome) };
    private readonly NavigationViewItem _devicesItem = new() { Content = "设备", Tag = "devices", Icon = Ui.Icon(Ui.GlyphDevices) };
    private readonly NavigationViewItem _snippetsItem = new() { Content = "代码片段", Tag = "snippets", Icon = Ui.Icon(Ui.GlyphSnippet) };
    private readonly NavigationViewItem _forwardsItem = new() { Content = "端口转发", Tag = "forwards", Icon = Ui.Icon(Ui.GlyphForward) };
    private readonly Dictionary<string, Func<FrameworkElement>> _pageFactories;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private string _currentPage = "home";
    private SessionTab? _activeTab;
    private SessionTab? _splitLeft, _splitRight;
    private WebPane? _assistant;
    private string _assistantUrl = "";
    private bool _closingConfirmed;

    public static nint WindowHandle { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        WindowHandle = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 860));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        _sessions = new SessionManager(_app);
        _sessions.Added += OnSessionAdded;
        _sessions.Removed += OnSessionRemoved;
        _sessions.Changed += OnSessionChanged;
        _sessions.CommandPaletteRequested += () => OpenCommandPalette();

        _pageFactories = new()
        {
            ["home"] = () => new HomePage(_sessions),
            ["devices"] = () => new DevicesPage(_sessions),
            ["keys"] = () => new KeysPage(),
            ["settings"] = () => new SettingsPage(),
            ["snippets"] = () => new SnippetsPage(_sessions),
            ["forwards"] = () => new PortForwardsPage(),
        };

        _sessions.AskAssistantRequested += AskAssistant;
        _sessions.BroadcastChanged += OnBroadcastChanged;
        _app.DevicesChanged += RebuildSidebar;
        _app.SettingsChanged += ApplyTheme;
        _app.SettingsChanged += ApplyAssistantUrl;

        var assistantHandle = new Controls.ResizeHandle();
        assistantHandle.Dragged += delta =>
        {
            // The pane sits on the right edge, so dragging left makes it wider.
            var settings = _app.Store.Data.Settings;
            settings.AssistantWidth = Math.Clamp(settings.AssistantWidth - delta, 300, 1000);
            AssistantHost.Width = settings.AssistantWidth;
        };
        assistantHandle.DragEnded += () => _app.Store.Save();
        AssistantHandleHost.Children.Add(assistantHandle);
        _app.PromptHandler = OnSshPrompt;
        AppWindow.Closing += OnWindowClosing;

        // The title bar sizes its content to the content, and the suggestion list follows the box's width.
        RootGrid.SizeChanged += (_, e) => Palette.Width = Math.Clamp(e.NewSize.Width - 520, 200, 600);
        RootGrid.Loaded += OnLoaded;
        ApplyTheme();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dialogs.Root = RootGrid.XamlRoot;
        if (Nav.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = "设置";
        RebuildSidebar();
        Navigate("home");

        if (!SshRunner.IsOpenSshAvailable)
            ShowNotice("未找到 OpenSSH 客户端", "SSH、SFTP 和 Mosh 需要它。请在“设置 > 系统 > 可选功能”中添加 OpenSSH 客户端。", InfoBarSeverity.Warning);

        await _app.ScanLocalAsync();
#if DEBUG
        if (SelfTest.IsEnabled)
        {
            await SelfTest.RunAsync(this, RootGrid, _sessions);
            return;
        }
#endif
        if (_app.Store.Data.Settings.RestoreSessions) _sessions.RestoreSavedTabs();
        _app.StartAutoForwards();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _app.Store.Data.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private Task<string?> OnSshPrompt(AskPassRequest request)
    {
        var result = new TaskCompletionSource<string?>();
        _app.Dispatcher.TryEnqueue(async () =>
        {
            try
            {
                result.TrySetResult(await Dialogs.AnswerSshPromptAsync(request, message => ShowNotice("请确认安全密钥", message, InfoBarSeverity.Informational)));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        return result.Task;
    }

    public void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingConfirmed) return;
        _sessions.SaveOpenTabs();
        int running = _sessions.Tabs.Count(t => t.HasForegroundWork);
        if (running == 0 || !_app.Store.Data.Settings.ConfirmCloseRunning)
        {
            _app.StopBackgroundWork();
            _sessions.CloseAll();
            return;
        }
        args.Cancel = true;
        if (await Dialogs.ConfirmAsync("关闭 EasyTerminal？", $"还有 {running} 个运行中的会话，关闭后它们都会被终止。", "全部关闭", destructive: true))
        {
            _closingConfirmed = true;
            _app.StopBackgroundWork();
            _sessions.CloseAll();
            Close();
        }
    }

    // ---- Sidebar: one folder per machine that has open sessions, each session a tab -----------

    private void RebuildSidebar()
    {
        Nav.MenuItems.Clear();
        _tabItems.Clear();
        Nav.MenuItems.Add(_homeItem);
        Nav.MenuItems.Add(_devicesItem);
        Nav.MenuItems.Add(_snippetsItem);
        Nav.MenuItems.Add(_forwardsItem);

        var localTabs = _sessions.Tabs.Where(t => t.IsLocal).ToList();
        var deviceGroups = _sessions.Tabs
            .Where(t => !t.IsLocal && t.Device != null)
            .GroupBy(t => t.Device!)
            .OrderBy(g => _app.Store.Data.Devices.IndexOf(g.Key))
            .ToList();

        if (localTabs.Count > 0 || deviceGroups.Count > 0) Nav.MenuItems.Add(new NavigationViewItemSeparator());
        if (localTabs.Count > 0)
            AddFolder(LocalFolderKey, "本机", Ui.GlyphComputer, null, localTabs);
        foreach (var group in deviceGroups)
            AddFolder("device:" + group.Key.Id, group.Key.Name, Ui.GlyphServer, group.Key, group.ToList());

        UpdateSelection();
    }

    private void AddFolder(string key, string name, string glyph, Device? device, List<SessionTab> tabs)
    {
        var title = Ui.Body(name);
        title.VerticalAlignment = VerticalAlignment.Center;
        var header = Ui.Columns("*", "auto");
        header.Children.Add(title.InColumn(0));
        Button add = null!;
        add = Ui.IconButton(Ui.GlyphAdd, $"在 {name} 上新建会话", () => CreateNewSessionMenu(device).ShowAt(add));
        add.Padding = new Thickness(6, 4, 6, 4);
        header.Children.Add(add.InColumn(1));

        var folder = new NavigationViewItem
        {
            Content = header,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = "folder:" + key,
            Icon = Ui.Icon(glyph),
            SelectsOnInvoked = false,
            IsExpanded = !_collapsedFolders.Contains(key),
        };
        foreach (var tab in tabs) folder.MenuItems.Add(BuildTabItem(tab));
        Nav.MenuItems.Add(folder);
    }

    private NavigationViewItem BuildTabItem(SessionTab tab)
    {
        var title = Ui.Body(tab.Title);
        title.VerticalAlignment = VerticalAlignment.Center;
        var close = Ui.IconButton(Ui.GlyphClose, "关闭会话", () => _ = CloseTabAsync(tab));
        close.Padding = new Thickness(6, 4, 6, 4);
        close.Opacity = 0;
        var content = Ui.Columns("*", "auto");
        content.Children.Add(title.InColumn(0));
        content.Children.Add(close.InColumn(1));

        var item = new NavigationViewItem
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = tab,
            Icon = Ui.Icon(tab.Glyph),
        };
        item.PointerEntered += (_, _) => close.Opacity = 1;
        item.PointerExited += (_, _) => close.Opacity = 0;
        close.GotFocus += (_, _) => close.Opacity = 1;
        close.LostFocus += (_, _) => close.Opacity = 0;

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("重新连接", Ui.GlyphRefresh, tab.Restart));
        menu.Items.Add(MenuItem("复制此会话", Ui.GlyphCopy, () => _sessions.Duplicate(tab)));
        bool inSplit = tab == _splitLeft || tab == _splitRight;
        menu.Items.Add(MenuItem(inSplit ? "取消分屏" : "在右侧分屏显示", Ui.GlyphSplit, () =>
        {
            if (inSplit) CloseSplit();
            else ShowSplit(tab);
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("关闭", Ui.GlyphClose, () => _ = CloseTabAsync(tab)));
        menu.Items.Add(MenuItem("关闭其他会话", Ui.GlyphClose, () => _ = CloseOthersAsync(tab)));
        item.ContextFlyout = menu;

        _tabItems[tab] = (item, title);
        ApplyTabState(tab);
        return item;
    }

    private void ApplyTabState(SessionTab tab)
    {
        if (!_tabItems.TryGetValue(tab, out var entry)) return;
        entry.Title.Text = tab.Title;
        entry.Item.Opacity = tab.IsLive ? 1 : 0.6;
        ToolTipService.SetToolTip(entry.Item, $"{tab.Title}\n{tab.StatusText}");
    }

    /// <summary>What can be opened on this machine; a null device means this computer.</summary>
    public MenuFlyout CreateNewSessionMenu(Device? device)
    {
        var menu = new MenuFlyout();
        if (device == null)
        {
            foreach (var kind in new[] { LocalKind.Shell, LocalKind.Wsl, LocalKind.Docker, LocalKind.Multipass })
            {
                var targets = _app.LocalTargets(kind).ToList();
                if (targets.Count == 0) continue;
                if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
                foreach (var target in targets)
                    menu.Items.Add(MenuItem(target.Name, Ui.GlyphFor(kind), () => _ = _sessions.OpenLocalAsync(target)));
            }
            return menu;
        }

        foreach (var connection in device.Connections.Where(device.Shows))
            menu.Items.Add(MenuItem(connection.Name, Ui.GlyphFor(connection.Protocol), () => _ = _sessions.OpenConnectionAsync(device, connection)));

        if (_app.Probes.TryGetValue(device.Id, out var probe) && probe.Sessions.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var session in probe.Sessions)
            {
                string kind = session.Kind == RemoteSessionKind.Tmux ? "tmux" : "herdr";
                menu.Items.Add(MenuItem($"{session.Name}（{kind}）", Ui.GlyphSessions, () => _ = _sessions.OpenRemoteSessionAsync(device, session)));
            }
        }

        foreach (var container in DockerContainers(device))
            menu.Items.Add(MenuItem(container.Name + "（Docker）", Ui.GlyphContainer, () => _ = _sessions.OpenRemoteContainerAsync(device, container)));

        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("添加连接…", Ui.GlyphAdd, () => _ = AddConnectionAsync(device)));
        return menu;
    }

    private IReadOnlyList<LocalTarget> DockerContainers(Device device) =>
        _app.Probes.TryGetValue(device.Id, out var probe) ? probe.Containers : Array.Empty<LocalTarget>();

    private async Task AddConnectionAsync(Device device)
    {
        if (await ConnectionDialog.ShowAsync(device, null) is { } connection)
        {
            device.Connections.Add(connection);
            _app.NotifyDevicesChanged();
        }
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = Ui.Icon(glyph) };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OnNavExpanding(NavigationView sender, NavigationViewItemExpandingEventArgs args)
    {
        if (args.ExpandingItemContainer?.Tag is string tag && tag.StartsWith("folder:")) _collapsedFolders.Remove(tag[7..]);
    }

    private void OnNavCollapsed(NavigationView sender, NavigationViewItemCollapsedEventArgs args)
    {
        if (args.CollapsedItemContainer?.Tag is string tag && tag.StartsWith("folder:")) _collapsedFolders.Add(tag[7..]);
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate("settings");
            return;
        }

        switch (args.InvokedItemContainer?.Tag)
        {
            case string page when !page.StartsWith("folder:"):
                Navigate(page);
                break;
            case string:
                if (args.InvokedItemContainer is NavigationViewItem folder) folder.IsExpanded = !folder.IsExpanded;
                break;
            case SessionTab tab:
                ActivateTab(tab);
                break;
        }
    }

    private void UpdateSelection()
    {
        if (_activeTab != null && _tabItems.TryGetValue(_activeTab, out var entry))
        {
            Nav.SelectedItem = entry.Item;
            return;
        }
        Nav.SelectedItem = _activeTab != null ? null : _currentPage switch
        {
            "home" => _homeItem,
            "devices" => _devicesItem,
            "snippets" => _snippetsItem,
            "forwards" => _forwardsItem,
            "settings" => Nav.SettingsItem,
            _ => Nav.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == _currentPage),
        };
    }

    public void Navigate(string name)
    {
        _currentPage = name;
        _activeTab = null;
        if (!_pages.TryGetValue(name, out var page))
        {
            page = _pageFactories[name]();
            _pages[name] = page;
        }
        PageHost.Children.Clear();
        PageHost.Children.Add(page);
        PageHost.Visibility = Visibility.Visible;
        SessionHost.Visibility = Visibility.Collapsed;
        foreach (var tab in _sessions.Tabs) tab.SetActive(false);
        (page as IRefreshablePage)?.Refresh();
        UpdateSelection();
    }

    // ---- AI assistant ------------------------------------------------------------------------

    private void ToggleAssistant() => SetAssistantOpen(AssistantHost.Visibility != Visibility.Visible);

    // The toggle flips itself on click; the pane's state is what decides how it ends up.
    private void OnAssistantButtonClick(object sender, RoutedEventArgs e) => ToggleAssistant();

    // ---- Snippets in the title bar -----------------------------------------------------------

    private void OnSnippetsButtonClick(object sender, RoutedEventArgs e) => ShowSnippetFlyout();

    /// <summary>A quick list to send a snippet to the terminal in front, without leaving it.</summary>
    internal Flyout ShowSnippetFlyout()
    {
        var target = _activeTab is { Terminal.State: Core.Terminal.SessionState.Running } tab ? tab : null;
        var search = new TextBox { PlaceholderText = "搜索片段" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxHeight = 360 };
        var hint = Ui.Secondary(target != null ? "点击发送到 " + target.Title : "没有正在运行的终端，点击会先提示打开一个", true);
        var flyout = new Flyout();

        void Fill()
        {
            list.Items.Clear();
            string query = search.Text.Trim();
            foreach (var snippet in _app.Store.Data.Snippets
                .Where(s => query.Length == 0 || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.LastUsed).ThenBy(s => s.Name))
            {
                var item = new StackPanel { Padding = new Thickness(0, 4, 0, 4), Tag = snippet };
                item.Children.Add(Ui.Body(snippet.Name));
                item.Children.Add(new TextBlock
                {
                    Text = snippet.Body.ReplaceLineEndings(" ⏎ "),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Ui.Brush("TextFillColorSecondaryBrush"),
                });
                list.Items.Add(item);
            }
            if (list.Items.Count == 0) list.Items.Add(Ui.Secondary(_app.Store.Data.Snippets.Count == 0 ? "还没有片段。" : "没有匹配的片段。"));
        }
        search.TextChanged += (_, _) => Fill();
        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not StackPanel { Tag: Snippet snippet }) return;
            flyout.Hide();
            if (target == null)
            {
                ShowNotice("没有正在运行的终端", "先打开一个终端会话，再运行片段。", InfoBarSeverity.Informational);
                return;
            }
            ActivateTab(target);
            _ = SnippetRunner.SendAsync(snippet, new[] { target });
        };
        Fill();

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(Ui.Button("新建片段", Ui.GlyphAdd, () =>
        {
            flyout.Hide();
            Navigate("snippets");
            _ = ((SnippetsPage)_pages["snippets"]).AddAsync();
        }));
        footer.Children.Add(Ui.Button("管理片段", Ui.GlyphSnippet, () =>
        {
            flyout.Hide();
            Navigate("snippets");
        }));

        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(search);
        panel.Children.Add(hint);
        panel.Children.Add(list);
        panel.Children.Add(footer);
        flyout.Content = panel;
        // A remote desktop window would cover the flyout.
        flyout.Opened += (_, _) => RdpPane.Suppress(true);
        flyout.Closed += (_, _) => RdpPane.Suppress(false);
        flyout.ShowAt(SnippetsButton);
        _app.Dispatcher.TryEnqueue(() => search.Focus(FocusState.Programmatic));
        return flyout;
    }

#if DEBUG
    internal void ShowAssistantForTest() => ToggleAssistant();
    internal WebPane? AssistantForTest => _assistant;
#endif

    private void SetAssistantOpen(bool open)
    {
        if (open && _assistant == null)
        {
            _assistantUrl = _app.Store.Data.Settings.AssistantUrl;
            _assistant = new WebPane(_assistantUrl, () => SetAssistantOpen(false));
            AssistantHost.Children.Add(_assistant);
        }
        AssistantHost.Width = _app.Store.Data.Settings.AssistantWidth;
        // Closing only hides the pane, so a conversation in progress is still there next time.
        AssistantHost.Visibility = AssistantHandleHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AssistantButton.IsChecked = open;
    }

    private void ApplyAssistantUrl()
    {
        string url = _app.Store.Data.Settings.AssistantUrl;
        if (_assistant == null || url == _assistantUrl) return;
        _assistantUrl = url;
        _assistant.Navigate(url);
    }

    private void AskAssistant(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetAssistantOpen(true);
        ShowNotice("已复制选中内容", "在 AI 助手的输入框里粘贴（Ctrl+V）即可提问。", InfoBarSeverity.Informational);
    }

    // ---- Broadcast and split view -------------------------------------------------------------

    private void OnBroadcastChanged()
    {
        if (_sessions.IsBroadcasting) ShowNotice("广播输入已开启", "在任一终端输入的内容都会发送到所有运行中的终端。可在命令面板中关闭。", InfoBarSeverity.Warning);
        else Notice.IsOpen = false;
    }

    /// <summary>Shows a tab to the right of the one in front, so two sessions can be seen at once.</summary>
    public void ShowSplit(SessionTab tab)
    {
        var left = _activeTab != null && _activeTab != tab ? _activeTab : _sessions.Tabs.LastOrDefault(t => t != tab);
        if (left == null)
        {
            ShowNotice("没有其他会话", "分屏需要至少两个会话。", InfoBarSeverity.Informational);
            return;
        }
        _splitLeft = left;
        _splitRight = tab;
        RebuildSidebar();
        ActivateTab(left);
    }

    public void CloseSplit()
    {
        var keep = _activeTab;
        _splitLeft = _splitRight = null;
        RebuildSidebar();
        if (keep != null) ActivateTab(keep);
    }

    private void LayoutSplit()
    {
        SessionHost.ColumnDefinitions[1].Width = _splitRight != null ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        foreach (var tab in _sessions.Tabs) Grid.SetColumn(tab.Content, tab == _splitRight ? 1 : 0);
    }

    // ---- Sessions ----------------------------------------------------------------------------

    private void OnSessionAdded(SessionTab tab)
    {
        SessionHost.Children.Add(tab.Content);
        tab.Content.Visibility = Visibility.Collapsed;
        // In split view the other pane takes over as "current" as soon as it is clicked.
        tab.Content.GotFocus += (_, _) =>
        {
            if (_activeTab == tab || (tab != _splitLeft && tab != _splitRight)) return;
            _activeTab = tab;
            UpdateSelection();
        };
        _activeTab = tab;
        RebuildSidebar();
        ActivateTab(tab);
    }

    private void OnSessionRemoved(SessionTab tab)
    {
        SessionHost.Children.Remove(tab.Content);
        if (tab == _splitLeft || tab == _splitRight)
        {
            _splitLeft = _splitRight = null;
            LayoutSplit();
        }
        bool wasActive = _activeTab == tab;
        if (wasActive) _activeTab = null;
        RebuildSidebar();
        if (!wasActive)
        {
            (PageHost.Children.FirstOrDefault() as IRefreshablePage)?.Refresh();
            return;
        }

        // Prefer a neighbour on the same machine so closing a tab keeps the user where they were.
        var next = _sessions.Tabs.LastOrDefault(t => t.Device == tab.Device && t.IsLocal == tab.IsLocal) ?? _sessions.Tabs.LastOrDefault();
        if (next != null) ActivateTab(next);
        else Navigate("home");
    }

    private void OnSessionChanged(SessionTab tab)
    {
        ApplyTabState(tab);
        (PageHost.Children.FirstOrDefault() as IRefreshablePage)?.Refresh();
    }

    public void ActivateTab(SessionTab tab)
    {
        _activeTab = tab;
        // Picking a third session replaces the left pane; the right one stays until split view is closed.
        if (_splitRight != null && tab != _splitRight) _splitLeft = tab;
        PageHost.Visibility = Visibility.Collapsed;
        SessionHost.Visibility = Visibility.Visible;
        LayoutSplit();
        foreach (var other in _sessions.Tabs) other.SetActive(other == tab || other == _splitLeft || other == _splitRight);
        UpdateSelection();
        // The sidebar takes focus while handling the click, so hand it back afterwards.
        _app.Dispatcher.TryEnqueue(() => tab.View?.FocusTerminal());
    }

    private async Task CloseTabAsync(SessionTab tab)
    {
        if (tab.HasForegroundWork && _app.Store.Data.Settings.ConfirmCloseRunning &&
            !await Dialogs.ConfirmAsync($"关闭 {tab.Title}？", "会话仍在运行，关闭后连接或进程会被终止。", "关闭", destructive: true))
            return;
        _sessions.Close(tab);
    }

    private async Task CloseOthersAsync(SessionTab keep)
    {
        var others = _sessions.Tabs.Where(t => t != keep).ToList();
        if (others.Count == 0) return;
        if (await Dialogs.ConfirmAsync("关闭其他会话？", $"将关闭 {others.Count} 个会话。", "关闭", destructive: true))
            foreach (var tab in others) _sessions.Close(tab);
    }
}

public interface IRefreshablePage
{
    void Refresh();
}
