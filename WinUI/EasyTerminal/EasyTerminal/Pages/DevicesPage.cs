using EasyTerminal.Controls;
using EasyTerminal.Core.Local;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EasyTerminal.Pages;

/// <summary>
/// The machine list and the place every new session starts: this computer, saved devices in nested
/// folders, filtered by tags, and what was found on its own (WSL distributions and Proxmox guests).
/// </summary>
public sealed class DevicesPage : UserControl, IRefreshablePage
{
    private const string DiscoveredKey = "::discovered";
    private const string DragPrefix = "easyterminal-device:";
    private const double Indent = 22;

    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly StackPanel _list = new() { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _quickConnect = new() { PlaceholderText = "user@host:22" };
    private readonly TextBox _filter = new() { PlaceholderText = "按名称、地址、文件夹、标签、系统或地区筛选" };
    private readonly WrapPanel _tagBar = new();
    /// <summary>Tags a device must all carry to be listed; clicking a tag on a card or in the bar toggles it.</summary>
    private readonly HashSet<string> _tagFilter = new(StringComparer.OrdinalIgnoreCase);

    public DevicesPage(SessionManager sessions)
    {
        _sessions = sessions;
        var (root, content) = Ui.Page();
        Content = root;

        var header = Ui.Columns("*", "auto", "auto", "auto");
        header.Children.Add(Ui.Title("设备").InColumn(0));
        header.Children.Add(Ui.Button("新建文件夹", Ui.GlyphFolder, () => _ = NewFolderAsync("")).InColumn(1));
        header.Children.Add(Ui.Button("从 SSH 配置导入", Ui.GlyphDownload, () => _ = ImportSshConfigAsync()).InColumn(2));
        header.Children.Add(Ui.Button("添加设备", Ui.GlyphAdd, () => _ = AddDeviceAsync(), accent: true).InColumn(3));
        content.Children.Add(header);

        var quick = Ui.Columns("*", "auto");
        quick.Children.Add(_quickConnect.InColumn(0));
        quick.Children.Add(Ui.Button("快速连接", Ui.GlyphPlay, QuickConnect).InColumn(1));
        _quickConnect.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) QuickConnect();
        };
        content.Children.Add(Ui.Card(quick));
        content.Children.Add(_filter);
        content.Children.Add(_tagBar);
        _filter.TextChanged += (_, _) => Refresh();
        content.Children.Add(_list);

        _app.DevicesChanged += Refresh;
        _app.ScanChanged += Refresh;
        Refresh();
    }

    private bool IsFiltering => _filter.Text.Trim().Length > 0 || _tagFilter.Count > 0;

    public void Refresh()
    {
        _list.Children.Clear();
        BuildTagBar();
        string query = _filter.Text.Trim();
        var matches = _app.Store.Data.Devices.Where(d => Matches(d, query)).ToList();
        var folders = _app.Store.Data.AllDeviceFolders();

        // Unfiled devices share a grid with this computer; each folder gets a heading, its devices, then its subfolders.
        var first = new CardGridPanel();
        if (!IsFiltering) first.Children.Add(BuildLocalCard());
        foreach (var device in matches.Where(d => d.Group.Length == 0)) first.Children.Add(BuildCard(device));
        if (first.Children.Count > 0) _list.Children.Add(first);
        foreach (var folder in folders.Where(f => FolderPath.Parent(f).Length == 0)) AddFolder(folder, 0, folders, matches);

        bool anyDiscovered = _tagFilter.Count == 0 && AddDiscovered(query);
        if (IsFiltering && matches.Count == 0 && !anyDiscovered) _list.Children.Add(Ui.Secondary("没有匹配的设备。"));
    }

    private bool Matches(Device device, string query)
    {
        if (_tagFilter.Any(t => !device.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        if (query.Length == 0) return true;
        var fields = new[] { device.Name, device.Host, device.User, device.Group, device.Facts?.Os ?? "", device.Facts?.CountryCode ?? "", device.Facts?.City ?? "" }.Concat(device.Tags);
        return fields.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsCollapsed(string key) => !IsFiltering && _app.Store.Data.CollapsedFolders.Contains(key);

    private void ToggleCollapsed(string key)
    {
        var collapsed = _app.Store.Data.CollapsedFolders;
        if (!collapsed.Remove(key)) collapsed.Add(key);
        _app.Store.Save();
        Refresh();
    }

    // ---- Tags --------------------------------------------------------------------------------

    private void BuildTagBar()
    {
        _tagBar.Children.Clear();
        var tags = _app.Store.Data.AllDeviceTags();
        _tagFilter.RemoveWhere(t => !tags.Any(x => x.Tag.Equals(t, StringComparison.OrdinalIgnoreCase)));
        _tagBar.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tag, count) in tags) _tagBar.Children.Add(TagPill(tag, count));
        if (_tagFilter.Count > 0) _tagBar.Children.Add(Ui.Chip(Ui.GlyphClose, "清除标签筛选", "显示所有设备", () =>
        {
            _tagFilter.Clear();
            Refresh();
        }));
    }

    /// <summary>A tag as a small toggle; selected tags narrow the list to devices that carry them all.</summary>
    private FrameworkElement TagPill(string tag, int? count = null)
    {
        bool selected = _tagFilter.Contains(tag);
        var button = new Button
        {
            Content = new TextBlock { Text = "# " + tag + (count is { } n ? $"  {n}" : ""), FontSize = 11 },
            Padding = new Thickness(8, 2, 8, 3),
            MinHeight = 0,
            MinWidth = 0,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(selected ? 0 : 1),
        };
        if (selected) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ToolTipService.SetToolTip(button, selected ? $"取消按“{tag}”筛选" : $"只看标签为“{tag}”的设备");
        button.Click += (_, _) =>
        {
            if (!_tagFilter.Remove(tag)) _tagFilter.Add(tag);
            Refresh();
        };
        return button;
    }

    public static List<string> ParseTags(string text) =>
        text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('#').Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Tags already in use, as buttons that add or remove themselves from <paramref name="box"/>; null when there are none.</summary>
    public static FrameworkElement? TagSuggestions(TextBox box)
    {
        var known = AppServices.Current.Store.Data.AllDeviceTags();
        if (known.Count == 0) return null;
        var panel = new WrapPanel();
        panel.Children.Add(new TextBlock { Text = "已有标签：", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 6), Foreground = Ui.Brush("TextFillColorSecondaryBrush") });
        foreach (var (tag, _) in known)
        {
            var button = new Button { Content = new TextBlock { Text = "# " + tag, FontSize = 11 }, Padding = new Thickness(8, 2, 8, 3), MinHeight = 0, MinWidth = 0, CornerRadius = new CornerRadius(10) };
            button.Click += (_, _) =>
            {
                var current = ParseTags(box.Text);
                int existing = current.FindIndex(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) current.RemoveAt(existing);
                else current.Add(tag);
                box.Text = string.Join(", ", current);
            };
            panel.Children.Add(button);
        }
        return panel;
    }

    private async Task EditTagsAsync(Device device)
    {
        var box = new TextBox { Header = "标签（用逗号分隔）", Text = string.Join(", ", device.Tags), PlaceholderText = "例如：db, 上海" };
        var panel = new StackPanel { Spacing = 8, MinWidth = 400 };
        panel.Children.Add(box);
        if (TagSuggestions(box) is { } suggestions) panel.Children.Add(suggestions);
        var dialog = new ContentDialog { Title = $"{device.Name} 的标签", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        device.Tags = ParseTags(box.Text);
        _app.NotifyDevicesChanged();
    }

    // ---- Folders -----------------------------------------------------------------------------

    /// <summary>An editable list of folders for dialogs; type a new path such as 生产/上海 to make one.</summary>
    public static ComboBox FolderPicker(string current)
    {
        var picker = new ComboBox
        {
            Header = "文件夹（可选，用 / 分隔子文件夹）",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "例如：生产/上海",
        };
        foreach (var folder in AppServices.Current.Store.Data.AllDeviceFolders()) picker.Items.Add(folder);
        picker.Text = current;
        if (picker.Items.Contains(current)) picker.SelectedItem = current;
        return picker;
    }

    private void AddFolder(string folder, int depth, List<string> folders, List<Device> matches)
    {
        var inside = matches.Where(d => FolderPath.IsWithin(d.Group, folder)).ToList();
        if (IsFiltering && inside.Count == 0) return;
        bool collapsed = IsCollapsed(folder);
        _list.Children.Add(SectionHeader(folder, Ui.GlyphFolder, FolderPath.Leaf(folder), inside.Count, depth, collapsed, menu => FillFolderMenu(menu, folder), dropFolder: folder));
        if (collapsed) return;

        var grid = new CardGridPanel { Margin = new Thickness(depth * Indent, 0, 0, 0) };
        foreach (var device in inside.Where(d => d.Group == folder)) grid.Children.Add(BuildCard(device));
        if (grid.Children.Count > 0) _list.Children.Add(grid);
        var children = folders.Where(f => FolderPath.Parent(f) == folder).ToList();
        foreach (var child in children) AddFolder(child, depth + 1, folders, matches);
        if (!IsFiltering && inside.Count == 0 && children.Count == 0)
        {
            var empty = Ui.Secondary("空文件夹。把设备卡片拖到标题上，或在设备的“更多”菜单里移动到这里。", true);
            empty.Margin = new Thickness(depth * Indent + 30, 0, 0, 4);
            _list.Children.Add(empty);
        }
    }

    /// <summary>A foldable heading: chevron, icon, name and count, with a menu on the right. A folder heading accepts dropped device cards.</summary>
    private FrameworkElement SectionHeader(string key, string glyph, string title, int count, int depth, bool collapsed, Action<MenuFlyout>? menu, string? dropFolder = null, bool busy = false)
    {
        var row = Ui.Columns("*", "auto");
        row.Margin = new Thickness(depth * Indent, depth == 0 ? 12 : 4, 0, 2);

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = collapsed ? "" : "", FontSize = 10, Foreground = Ui.Brush("TextFillColorSecondaryBrush") });
        label.Children.Add(Ui.Icon(glyph, 14));
        label.Children.Add(Ui.Strong(title));
        label.Children.Add(Ui.Secondary(count.ToString()));
        if (busy) label.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        var toggle = new Button
        {
            Content = label,
            Padding = new Thickness(4, 4, 10, 4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTipService.SetToolTip(toggle, (collapsed ? "展开 " : "折叠 ") + (dropFolder ?? title));
        toggle.Click += (_, _) => ToggleCollapsed(key);
        row.Children.Add(toggle.InColumn(0));
        if (menu != null) row.Children.Add(Ui.MenuButton(Ui.GlyphMore, "文件夹操作", menu).InColumn(1));

        if (dropFolder != null) AcceptDevices(row, dropFolder);
        return row;
    }

    private void FillFolderMenu(MenuFlyout menu, string folder)
    {
        menu.Items.Add(Ui.MenuItem("在此添加设备", Ui.GlyphAdd, () => _ = AddDeviceAsync(folder)));
        menu.Items.Add(Ui.MenuItem("新建子文件夹", Ui.GlyphFolder, () => _ = NewFolderAsync(folder)));
        menu.Items.Add(Ui.MenuItem("重命名或移动", Ui.GlyphRename, () => _ = RenameFolderAsync(folder)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.MenuItem("删除文件夹", Ui.GlyphDelete, () => _ = DeleteFolderAsync(folder)));
    }

    /// <summary>Asks for a folder name under <paramref name="parent"/>; returns its full path, or null when cancelled.</summary>
    private async Task<string?> NewFolderAsync(string parent)
    {
        string title = parent.Length == 0 ? "新建文件夹" : $"在“{parent}”中新建文件夹";
        string? name = await Dialogs.PromptTextAsync(title, "名称（用 / 可一次创建多层）", "", "创建", "例如：生产");
        string path = FolderPath.Combine(parent, name ?? "");
        if (name == null || path.Length == 0 || path == parent) return null;
        if (!_app.Store.Data.DeviceFolders.Contains(path)) _app.Store.Data.DeviceFolders.Add(path);
        _app.Store.Data.CollapsedFolders.RemoveAll(f => FolderPath.SelfAndAncestors(path).Contains(f));
        _app.NotifyDevicesChanged();
        return path;
    }

    private async Task RenameFolderAsync(string folder)
    {
        string? input = await Dialogs.PromptTextAsync("重命名文件夹", "新的路径（用 / 分隔，可移到别的文件夹下）", folder, "保存");
        string target = FolderPath.Normalize(input);
        if (input == null || target.Length == 0 || target == folder) return;
        if (FolderPath.IsWithin(target, folder))
        {
            await Dialogs.AlertAsync("无法移动", "不能把文件夹移到它自己里面。");
            return;
        }
        var data = _app.Store.Data;
        foreach (var device in data.Devices) device.Group = FolderPath.Rename(device.Group, folder, target);
        data.DeviceFolders = data.DeviceFolders.Select(f => FolderPath.Rename(f, folder, target)).Distinct().ToList();
        data.CollapsedFolders = data.CollapsedFolders.Select(f => FolderPath.Rename(f, folder, target)).Distinct().ToList();
        foreach (var widget in data.Settings.HomeWidgets ?? new()) widget.Group = FolderPath.Rename(widget.Group, folder, target);
        _app.NotifyDevicesChanged();
    }

    /// <summary>Removes the folder and its subfolders; their devices move up to the folder above.</summary>
    private async Task DeleteFolderAsync(string folder)
    {
        var data = _app.Store.Data;
        string parent = FolderPath.Parent(folder);
        var inside = data.Devices.Where(d => FolderPath.IsWithin(d.Group, folder)).ToList();
        string destination = parent.Length == 0 ? "顶层" : $"“{parent}”";
        string message = inside.Count == 0 ? "文件夹是空的。" : $"其中的 {inside.Count} 台设备会移到{destination}，设备本身不会删除。";
        if (!await Dialogs.ConfirmAsync($"删除文件夹 {folder}？", message, "删除", destructive: true)) return;
        foreach (var device in inside) device.Group = parent;
        data.DeviceFolders.RemoveAll(f => FolderPath.IsWithin(f, folder));
        data.CollapsedFolders.RemoveAll(f => FolderPath.IsWithin(f, folder));
        _app.NotifyDevicesChanged();
    }

    private void MoveDevice(Device device, string folder)
    {
        if (device.Group == folder) return;
        device.Group = folder;
        _app.NotifyDevicesChanged();
    }

    private MenuFlyoutSubItem MoveMenu(Device device)
    {
        var sub = new MenuFlyoutSubItem { Text = "移到文件夹", Icon = Ui.Icon(Ui.GlyphFolder) };
        if (device.Group.Length > 0) sub.Items.Add(Ui.MenuItem("不放在文件夹里", Ui.GlyphUp, () => MoveDevice(device, "")));
        foreach (var folder in _app.Store.Data.AllDeviceFolders())
        {
            int depth = folder.Count(c => c == FolderPath.Separator);
            var item = Ui.MenuItem(new string('　', depth) + FolderPath.Leaf(folder), folder == device.Group ? Ui.GlyphAccept : Ui.GlyphFolder, () => MoveDevice(device, folder));
            item.IsEnabled = folder != device.Group;
            ToolTipService.SetToolTip(item, folder);
            sub.Items.Add(item);
        }
        sub.Items.Add(new MenuFlyoutSeparator());
        sub.Items.Add(Ui.MenuItem("新建文件夹…", Ui.GlyphAdd, async () =>
        {
            if (await NewFolderAsync("") is { } created) MoveDevice(device, created);
        }));
        return sub;
    }

    /// <summary>Device cards can be dragged; a folder heading takes them in.</summary>
    private static void MakeDraggable(UIElement card, Device device)
    {
        card.CanDrag = true;
        card.DragStarting += (_, e) =>
        {
            e.Data.SetText(DragPrefix + device.Id);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
    }

    private void AcceptDevices(FrameworkElement target, string folder)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.Text)) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "移到 " + folder;
        };
        target.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.Text)) return;
            var deferral = e.GetDeferral();
            try
            {
                string text = await e.DataView.GetTextAsync();
                if (text.StartsWith(DragPrefix, StringComparison.Ordinal) && Guid.TryParse(text[DragPrefix.Length..], out var id) && _app.Store.FindDevice(id) is { } device)
                    MoveDevice(device, folder);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    // ---- Discovered: WSL and Proxmox guests --------------------------------------------------

    /// <summary>Adds what was found without being added by hand; returns whether anything matched.</summary>
    private bool AddDiscovered(string query)
    {
        bool Hit(params string[] fields) => query.Length == 0 || fields.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase));

        var wsl = _app.LocalTargets(LocalKind.Wsl).Where(t => Hit(t.Name, t.Detail, "wsl")).ToList();
        var hosts = _app.Store.Data.Devices
            .Where(d => d.Facts?.HasProxmox == true)
            .Select(d => (Host: d, Guests: _app.ProxmoxGuests(d).Where(g => Hit(g.Name, g.Id.ToString(), d.Name, g.Kind == PveGuestKind.Lxc ? "lxc" : "vm")).ToList()))
            .Where(h => h.Guests.Count > 0 || query.Length == 0)
            .ToList();
        int total = wsl.Count + hosts.Sum(h => h.Guests.Count);
        if (query.Length > 0 && total == 0) return false;

        bool busy = _app.IsScanning || hosts.Any(h => _app.ProbingDevices.Contains(h.Host.Id));
        bool collapsed = IsCollapsed(DiscoveredKey);
        _list.Children.Add(SectionHeader(DiscoveredKey, Ui.GlyphSearch, "自动发现", total, 0, collapsed, menu =>
        {
            var refresh = Ui.MenuItem(busy ? "正在扫描…" : "重新发现", Ui.GlyphRefresh, () => _ = _app.RefreshDiscoveryAsync());
            refresh.IsEnabled = !busy;
            menu.Items.Add(refresh);
        }, busy: busy));
        if (collapsed) return total > 0;

        if (wsl.Count > 0)
        {
            _list.Children.Add(SubHeading(Ui.GlyphLinux, "WSL", "本机的 Linux 发行版"));
            var grid = new CardGridPanel();
            foreach (var target in wsl) grid.Children.Add(BuildWslCard(target));
            _list.Children.Add(grid);
        }
        foreach (var (host, guests) in hosts)
        {
            var facts = host.Facts!;
            string node = _app.ProxmoxNode(host);
            string scanned = _app.Probes.ContainsKey(host.Id) ? "刚扫描" : $"扫描于 {Ui.RelativeTime(facts.ScannedAt)}";
            _list.Children.Add(SubHeading(Ui.GlyphVm, $"{host.Name} · Proxmox", (node.Length > 0 ? $"节点 {node} · " : "") + scanned));
            if (guests.Count == 0)
            {
                var none = Ui.Secondary("这个节点上没有虚拟机或容器，或者还需要重新扫描。", true);
                none.Margin = new Thickness(Indent, 0, 0, 4);
                _list.Children.Add(none);
                continue;
            }
            var grid = new CardGridPanel();
            foreach (var guest in guests) grid.Children.Add(BuildGuestCard(host, guest));
            _list.Children.Add(grid);
        }
        if (total == 0 && hosts.Count == 0)
        {
            string wslNote = _app.Scan?.Statuses.FirstOrDefault(s => s.Kind == LocalKind.Wsl) is { Installed: false } status ? $"WSL：{status.Message}。" : "";
            var none = Ui.Secondary(wslNote + "扫描过的 Proxmox 主机上的虚拟机和 LXC 容器也会显示在这里。", true);
            none.Margin = new Thickness(30, 0, 0, 4);
            _list.Children.Add(none);
        }
        return total > 0;
    }

    private static FrameworkElement SubHeading(string glyph, string title, string detail)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(Indent, 6, 0, 2) };
        row.Children.Add(Ui.Icon(glyph, 12));
        row.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        row.Children.Add(Ui.Secondary(detail));
        return row;
    }

    private static TextBlock StatusLine(string text, bool running)
    {
        var line = Ui.Secondary("");
        line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = running ? "● " : "○ ",
            Foreground = running ? Ui.Brush("SystemFillColorSuccessBrush") : Ui.Brush("TextFillColorTertiaryBrush"),
        });
        line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
        return line;
    }

    private FrameworkElement BuildWslCard(LocalTarget target)
    {
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Chip(Ui.GlyphTerminal, "终端", "打开 " + target.Name, () => _ = _sessions.OpenLocalAsync(target)));
        var body = new List<FrameworkElement> { StatusLine("WSL" + (target.Detail.Length > 0 ? " · " + target.Detail : "") + (target.IsRunning ? " · 运行中" : " · 已停止"), target.IsRunning), actions };
        var icon = MachineIcon(OsIdentity.LogoForName(target.Name) ?? "linux", Ui.GlyphLinux, $"WSL · {target.Name}", null, "");
        return BuildTile(icon, target.Name, $"WSL 发行版 {target.Name}\n{target.Detail}", false, body, menu =>
        {
            bool isDefault = _app.DefaultShell.StableKey == target.StableKey;
            var setDefault = Ui.MenuItem(isDefault ? "已是默认 Shell" : "设为默认 Shell", isDefault ? Ui.GlyphStarFilled : Ui.GlyphStar, () =>
            {
                _app.Store.Data.Settings.DefaultShellKey = target.StableKey;
                _app.NotifySettingsChanged();
                Refresh();
            });
            setDefault.IsEnabled = !isDefault;
            menu.Items.Add(setDefault);
            if (target.IsRunning) menu.Items.Add(Ui.MenuItem("停止", Ui.GlyphStop, () => _ = StopLocalAsync(target)));
            menu.Items.Add(new MenuFlyoutSeparator());
            var scan = Ui.MenuItem(_app.IsScanning ? "正在扫描…" : "重新扫描本机环境", Ui.GlyphRefresh, () => _ = _app.ScanLocalAsync());
            scan.IsEnabled = !_app.IsScanning;
            menu.Items.Add(scan);
        });
    }

    private FrameworkElement BuildGuestCard(Device host, PveGuest guest)
    {
        bool isLxc = guest.Kind == PveGuestKind.Lxc;
        var actions = new WrapPanel();
        actions.Children.Add(isLxc
            ? Ui.Chip(Ui.GlyphTerminal, "进入", $"在 {host.Name} 上进入容器 {guest.Id}", () => GuestActions.Enter(_sessions, host, guest))
            : Ui.Chip(Ui.GlyphGlobe, "控制台", "打开 Proxmox 网页控制台", () => GuestActions.Enter(_sessions, host, guest)));
        // A VM connected to once through its host is a saved device; its sessions open from here too.
        if (GuestActions.SavedDevice(host, guest) is { } saved)
            foreach (var connection in saved.Connections.Where(c => c.Protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Rdp))
                actions.Children.Add(Ui.Chip(Ui.GlyphFor(connection.Protocol), connection.Name, $"经 {host.Name} 连接 {saved.Host}", () => _ = _sessions.OpenConnectionAsync(saved, connection)));
        else if (!isLxc)
            actions.Children.Add(Ui.Chip(Ui.GlyphTerminal, "SSH", $"经 {host.Name} 跳转连接系统内部，并保存为设备", () => _ = GuestActions.OpenThroughHostAsync(_sessions, host, guest, ConnectionProtocol.Ssh)));

        string kind = guest.Detail.Length > 0 ? guest.Detail : isLxc ? "LXC" : "VM";
        var body = new List<FrameworkElement> { StatusLine($"{guest.Id} · {kind} · {GuestActions.DescribeStatus(guest)}", guest.IsRunning), actions };
        var icon = new Grid { Width = 34, Height = 34 };
        var mark = Ui.Icon(GuestActions.Glyph(guest), 22);
        mark.HorizontalAlignment = HorizontalAlignment.Left;
        mark.VerticalAlignment = VerticalAlignment.Top;
        icon.Children.Add(mark);
        bool probing = _app.ProbingDevices.Contains(host.Id);
        return BuildTile(icon, guest.Name, $"{guest.Name}（{(isLxc ? "LXC 容器" : "虚拟机")} {guest.Id}）\n位于 {host.Name}", probing, body,
            menu => GuestActions.AddMenuItems(menu.Items, _sessions, host, guest));
    }

    /// <summary>Scanning, monitoring and tunnels all need a shell over SSH, which RDP, Telnet and serial devices lack.</summary>
    public static bool SupportsSsh(Device device) =>
        device.Connections.Count == 0 || device.Connections.Any(c => c.Protocol is not (ConnectionProtocol.Rdp or ConnectionProtocol.Telnet or ConnectionProtocol.Serial));

    private async Task ImportSshConfigAsync()
    {
        string path = Path.Combine(KeyManager.DefaultDirectory, "config");
        if (!File.Exists(path))
        {
            await Dialogs.AlertAsync("没有 SSH 配置", $"没有找到 {path}。");
            return;
        }
        try
        {
            var devices = SshConfigImporter.Parse(await File.ReadAllTextAsync(path), _app.Store.Data.Devices);
            if (devices.Count == 0)
            {
                await Dialogs.AlertAsync("没有新设备", "配置文件里的主机都已经添加过了。");
                return;
            }
            if (!await Dialogs.ConfirmAsync($"导入 {devices.Count} 台设备？", string.Join("、", devices.Select(d => d.Name)), "导入")) return;
            _app.Store.Data.Devices.AddRange(devices);
            _app.NotifyDevicesChanged();
        }
        catch (IOException ex)
        {
            await Dialogs.AlertAsync("无法读取 SSH 配置", ex.Message);
        }
    }

    // ---- Quick connect -----------------------------------------------------------------------

    private void QuickConnect()
    {
        string text = _quickConnect.Text.Trim();
        if (text.Length == 0) return;
        if (!TryParseAddress(text, out var user, out var host, out var port))
        {
            _ = Dialogs.AlertAsync("地址无效", "请使用 user@host 或 user@host:port 的格式。");
            return;
        }

        _quickConnect.Text = "";
        _sessions.QuickConnect(user, host, port);
    }

    public static bool TryParseAddress(string text, out string user, out string host, out int port)
    {
        user = "";
        port = 22;
        host = text;
        int at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at];
            host = text[(at + 1)..];
        }
        int colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith(']') && int.TryParse(host[(colon + 1)..], out int parsed))
        {
            port = parsed;
            host = host[..colon];
        }
        host = host.Trim('[', ']');
        return host.Length > 0 && !host.Contains(' ') && port is > 0 and < 65536;
    }

    // ---- Cards -------------------------------------------------------------------------------

    // ---- Cards -------------------------------------------------------------------------------

    /// <summary>A device card: system logo and name, a More menu for managing it, then its tags and rows of session buttons.</summary>
    private static Border BuildTile(FrameworkElement icon, string name, string tooltip, bool busy, IEnumerable<FrameworkElement> body, Action<MenuFlyout> more)
    {
        var panel = new StackPanel { Spacing = 10 };

        var header = Ui.Columns("auto", "*", "auto");
        header.ColumnSpacing = 10;
        header.Children.Add(icon.InColumn(0));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var nameText = Ui.Strong(name);
        ToolTipService.SetToolTip(nameText, tooltip);
        title.Children.Add(nameText);
        if (busy)
        {
            var ring = new ProgressRing { IsActive = true, Width = 14, Height = 14 };
            ToolTipService.SetToolTip(ring, "正在扫描");
            title.Children.Add(ring);
        }
        header.Children.Add(title.InColumn(1));
        var moreButton = Ui.MenuButton(Ui.GlyphMore, "更多", more);
        moreButton.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(moreButton.InColumn(2));
        panel.Children.Add(header);

        foreach (var element in body) panel.Children.Add(element);
        return Ui.Card(panel, 12);
    }

    /// <summary>The system's logo, with the flag of where the machine is in the bottom-right corner.</summary>
    internal static FrameworkElement MachineIcon(string? logo, string fallbackGlyph, string systemTooltip, string? countryCode, string locationTooltip)
    {
        var host = new Grid { Width = 34, Height = 34, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        FrameworkElement mark = (logo != null ? Ui.Asset("Os", logo, 26, 26) : null) ?? (FrameworkElement)Ui.Icon(fallbackGlyph, 24);
        mark.HorizontalAlignment = HorizontalAlignment.Left;
        mark.VerticalAlignment = VerticalAlignment.Top;
        host.Children.Add(mark);
        ToolTipService.SetToolTip(host, systemTooltip);

        if (countryCode is { Length: 2 } && Ui.Asset("Flags", countryCode.ToLowerInvariant(), 16, 12) is { } flag)
        {
            var frame = new Border
            {
                Child = flag,
                CornerRadius = new CornerRadius(2),
                // A visible edge, or white flags such as Japan's melt into a light card.
                BorderThickness = new Thickness(1),
                BorderBrush = Ui.Brush("ControlStrongStrokeColorDefaultBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            ToolTipService.SetToolTip(frame, locationTooltip);
            host.Children.Add(frame);
        }
        return host;
    }


    // ---- This computer -----------------------------------------------------------------------

    /// <summary>Shells, Docker and Multipass. WSL distributions have cards of their own under 自动发现.</summary>
    private FrameworkElement BuildLocalCard()
    {
        var actions = new WrapPanel();
        var unavailable = new List<string>();
        var messages = new List<string>();
        foreach (var kind in new[] { LocalKind.Shell, LocalKind.Docker, LocalKind.Multipass })
        {
            var targets = _app.LocalTargets(kind).ToList();
            foreach (var target in targets) actions.Children.Add(BuildLocalChip(target));
            if (targets.Count == 0 && _app.Scan?.Statuses.FirstOrDefault(s => s.Kind == kind) is { } status)
            {
                unavailable.Add(Ui.Name(kind));
                messages.Add($"{Ui.Name(kind)}：{status.Message}");
            }
        }

        var body = new List<FrameworkElement> { actions };
        string tooltip = "本机 · " + Environment.MachineName + (unavailable.Count > 0 ? "\n不可用：\n" + string.Join("\n", messages) : "");
        var icon = MachineIcon("windows", Ui.GlyphComputer, $"Windows · {Environment.OSVersion.Version}", null, "");
        return BuildTile(icon, "本机", tooltip, _app.IsScanning, body, menu =>
        {
            var scan = Ui.MenuItem(_app.IsScanning ? "正在扫描…" : "重新扫描本机环境", Ui.GlyphRefresh, () => _ = _app.ScanLocalAsync());
            scan.IsEnabled = !_app.IsScanning;
            menu.Items.Add(scan);
        });
    }

    private FrameworkElement BuildLocalChip(LocalTarget target)
    {
        var menu = new List<MenuFlyoutItem>();
        string tooltip = target.Kind == LocalKind.Shell ? "Shell" : $"{Ui.Name(target.Kind)} · {target.Detail}";
        string glyph = Ui.GlyphFor(target.Kind);

        if (target.Kind == LocalKind.Shell)
        {
            if (_app.DefaultShell.StableKey == target.StableKey)
            {
                glyph = Ui.GlyphStarFilled;
                tooltip = "默认 Shell";
            }
            else
            {
                menu.Add(Ui.MenuItem("设为默认 Shell", Ui.GlyphStar, () =>
                {
                    _app.Store.Data.Settings.DefaultShellKey = target.StableKey;
                    _app.NotifySettingsChanged();
                    Refresh();
                }));
                tooltip += "（右键可设为默认）";
            }
        }
        else if (target.IsRunning)
        {
            menu.Add(Ui.MenuItem("停止", Ui.GlyphStop, () => _ = StopLocalAsync(target)));
        }
        else if (target.Kind != LocalKind.Wsl)
        {
            menu.Add(Ui.MenuItem("启动", Ui.GlyphRefresh, () => _ = RunLocalAction(() => LocalScanner.StartTargetAsync(target))));
        }
        return Ui.Chip(glyph, target.Name, tooltip, () => _ = _sessions.OpenLocalAsync(target), menu.ToArray());
    }

    private async Task StopLocalAsync(LocalTarget target)
    {
        if (await Dialogs.ConfirmAsync($"停止 {target.Name}？", "其中运行的所有进程都会结束。", "停止", destructive: true))
            await RunLocalAction(() => LocalScanner.StopTargetAsync(target));
    }

    private async Task RunLocalAction(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync("操作失败", ex.Message);
        }
        await _app.ScanLocalAsync();
    }


    // ---- Remote devices ----------------------------------------------------------------------

    private FrameworkElement BuildCard(Device device)
    {
        var actions = new WrapPanel();
        foreach (var connection in device.Connections.Where(device.Shows))
        {
            string detail = Ui.Name(connection.Protocol);
            if (connection.SessionName.Length > 0) detail += " · " + connection.SessionName;
            if (connection.OverrideNetwork) detail += " · " + DescribeRoute(connection.Network);
            actions.Children.Add(Ui.Chip(Ui.GlyphFor(connection.Protocol), connection.Name, detail + "（右键可编辑或删除）",
                () => _ = _sessions.OpenConnectionAsync(device, connection),
                Ui.MenuItem("编辑", Ui.GlyphEdit, () => _ = EditConnectionAsync(device, connection)),
                Ui.MenuItem("删除", Ui.GlyphDelete, () => _ = DeleteConnectionAsync(device, connection))));
        }

        var body = new List<FrameworkElement>();
        if (device.Tags.Count > 0)
        {
            var tags = new WrapPanel();
            foreach (var tag in device.Tags) tags.Children.Add(TagPill(tag));
            body.Add(tags);
        }
        body.Add(actions);
        bool probing = _app.ProbingDevices.Contains(device.Id);

        var lines = new List<string> { device.Name, DescribeEndpoint(device), DescribeAuth(device) + " · " + DescribeRoute(device.Network) };
        if (device.Group.Length > 0) lines.Add("文件夹：" + device.Group);
        if (device.AlternateAddresses.Count > 0) lines.Add("备用地址：" + string.Join("、", device.AlternateAddresses));
        if (device.Tags.Count > 0) lines.Add("标签：" + string.Join("、", device.Tags));
        var facts = device.Facts;
        bool desktopOnly = device.Connections.Count > 0 && device.Connections.All(c => c.Protocol == ConnectionProtocol.Rdp);
        var icon = MachineIcon(
            OsIdentity.LogoFor(facts) ?? (desktopOnly ? "windows" : null),
            Ui.GlyphServer,
            facts != null ? Ui.DescribeFacts(facts) + $"\n扫描于 {Ui.RelativeTime(facts.ScannedAt)}" : desktopOnly ? "Windows 远程桌面" : "还没有扫描，点“更多”里的扫描可识别系统",
            facts?.CountryCode,
            facts != null ? Ui.DescribeLocation(facts) : "");

        var card = BuildTile(icon, device.Name, string.Join("\n", lines), probing, body, menu =>
        {
            menu.Items.Add(Ui.MenuItem("添加连接", Ui.GlyphAdd, () => _ = AddConnectionAsync(device)));
            if (SupportsSsh(device))
            {
                var probe = Ui.MenuItem(probing ? "正在扫描…" : "扫描系统、软件和地区", Ui.GlyphRefresh, () => _ = ProbeAsync(device));
                probe.IsEnabled = !probing;
                menu.Items.Add(probe);
            }
            menu.Items.Add(Ui.MenuItem("编辑设备", Ui.GlyphEdit, () => _ = EditDeviceAsync(device)));
            menu.Items.Add(MoveMenu(device));
            menu.Items.Add(Ui.MenuItem("编辑标签", Ui.GlyphTag, () => _ = EditTagsAsync(device)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.MenuItem("删除设备", Ui.GlyphDelete, () => _ = DeleteDeviceAsync(device)));
        });
        MakeDraggable(card, device);
        return card;
    }


    private static string DescribeEndpoint(Device device) =>
        (device.User.Length > 0 ? device.User + "@" : "") + device.Host + (device.Port != 22 ? ":" + device.Port : "");

    private static string DescribeAuth(Device device) => device.Auth switch
    {
        AuthMethod.KeyFile => "密钥 " + Path.GetFileName(device.KeyPath),
        AuthMethod.Password => "密码",
        _ => "SSH Agent / 默认密钥",
    };

    private string DescribeRoute(NetworkProfile network)
    {
        var parts = new List<string>();
        if (network.ProxyKind == ProxyKind.Tailscale) parts.Add("内置 Tailscale");
        else if (network.ProxyKind != ProxyKind.None) parts.Add($"{network.ProxyKind} 代理 {network.ProxyHost}:{network.ProxyPort}");
        foreach (var id in network.JumpDeviceIds) parts.Add("跳板 " + (_app.Store.FindDevice(id)?.Name ?? "（已删除）"));
        return parts.Count == 0 ? "直连" : string.Join(" → ", parts);
    }

    public async Task AddDeviceAsync(string? folder = null)
    {
        if (await DeviceDialog.ShowAsync(null, folder) is not { } device) return;
        _app.Store.Data.Devices.Add(device);
        _app.NotifyDevicesChanged();
        ScanAfterChange(device);
    }

    private async Task EditDeviceAsync(Device device)
    {
        if (await DeviceDialog.ShowAsync(device) == null) return;
        _app.NotifyDevicesChanged();
        ScanAfterChange(device);
    }

    /// <summary>The address or login may have changed, so what is installed there is checked again.</summary>
    private void ScanAfterChange(Device device)
    {
        if (!SupportsSsh(device) || _app.ProbingDevices.Contains(device.Id)) return;
        _ = ScanQuietlyAsync(device);
    }

    private async Task ScanQuietlyAsync(Device device)
    {
        try
        {
            await _app.ProbeAsync(device);
        }
        catch (Exception ex)
        {
            // Not something the user started, so a failure is a notice rather than a dialog to dismiss.
            App.MainWindowInstance?.ShowNotice($"无法扫描 {device.Name}", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private static async Task<bool> ConfirmDeleteAsync(Device device)
    {
        var app = AppServices.Current;
        var dependents = app.Store.Data.Devices.Where(d => d != device && d.Network.JumpDeviceIds.Contains(device.Id)).Select(d => d.Name).ToList();
        string message = $"将删除它的 {device.Connections.Count} 个连接配置，不影响远程主机。";
        if (dependents.Count > 0) message += $"\n\n注意：{string.Join("、", dependents)} 把它用作跳板，删除后这些设备将无法连接。";
        return await Dialogs.ConfirmAsync($"删除设备 {device.Name}？", message, "删除", destructive: true);
    }

    private async Task DeleteDeviceAsync(Device device)
    {
        if (!await ConfirmDeleteAsync(device)) return;
        _app.Store.Secrets.Remove(device.PasswordSecretId);
        _app.Store.Data.Devices.Remove(device);
        _app.NotifyDevicesChanged();
    }

    private async Task AddConnectionAsync(Device device)
    {
        if (await ConnectionDialog.ShowAsync(device, null) is { } connection)
        {
            device.Connections.Add(connection);
            _app.NotifyDevicesChanged();
        }
    }

    private async Task EditConnectionAsync(Device device, Connection connection)
    {
        if (await ConnectionDialog.ShowAsync(device, connection) != null) _app.NotifyDevicesChanged();
    }

    private async Task DeleteConnectionAsync(Device device, Connection connection)
    {
        if (await Dialogs.ConfirmAsync($"删除连接 {connection.Name}？", "这只会删除保存的配置，不影响远程主机。", "删除", destructive: true))
        {
            device.Connections.Remove(connection);
            _app.NotifyDevicesChanged();
        }
    }

    private async Task ProbeAsync(Device device)
    {
        try
        {
            await _app.ProbeAsync(device);
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync($"无法扫描 {device.Name}", ex.Message);
        }
    }
}
