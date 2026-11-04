using EasyTerminal.Controls;
using EasyTerminal.Core.Local;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages;

/// <summary>The machine list and the place every new session starts: this computer and all saved devices.</summary>
public sealed class DevicesPage : UserControl, IRefreshablePage
{
    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly StackPanel _list = new() { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _quickConnect = new() { PlaceholderText = "user@host:22" };
    private readonly TextBox _filter = new() { PlaceholderText = "按名称、地址、分组、标签、系统或地区筛选" };

    public DevicesPage(SessionManager sessions)
    {
        _sessions = sessions;
        var (root, content) = Ui.Page();
        Content = root;

        var header = Ui.Columns("*", "auto", "auto");
        header.Children.Add(Ui.Title("设备").InColumn(0));
        header.Children.Add(Ui.Button("从 SSH 配置导入", Ui.GlyphDownload, () => _ = ImportSshConfigAsync()).InColumn(1));
        header.Children.Add(Ui.Button("添加设备", Ui.GlyphAdd, () => _ = AddDeviceAsync(), accent: true).InColumn(2));
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
        _filter.TextChanged += (_, _) => Refresh();
        content.Children.Add(_list);

        _app.DevicesChanged += Refresh;
        _app.ScanChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        _list.Children.Clear();
        string query = _filter.Text.Trim();
        var matches = _app.Store.Data.Devices.Where(d => Matches(d, query)).ToList();

        // Ungrouped devices share a grid with this computer; each named group gets its own heading and grid.
        var first = new CardGridPanel();
        if (query.Length == 0) first.Children.Add(BuildLocalCard());
        foreach (var device in matches.Where(d => d.Group.Length == 0)) first.Children.Add(BuildCard(device));
        if (first.Children.Count > 0) _list.Children.Add(first);
        foreach (var group in matches.Where(d => d.Group.Length > 0).GroupBy(d => d.Group))
        {
            _list.Children.Add(Ui.Section(group.Key));
            var grid = new CardGridPanel();
            foreach (var device in group) grid.Children.Add(BuildCard(device));
            _list.Children.Add(grid);
        }
        if (query.Length > 0 && matches.Count == 0) _list.Children.Add(Ui.Secondary("没有匹配的设备。"));
    }

    private static bool Matches(Device device, string query)
    {
        if (query.Length == 0) return true;
        var fields = new[] { device.Name, device.Host, device.User, device.Group, device.Facts?.Os ?? "", device.Facts?.CountryCode ?? "", device.Facts?.City ?? "" }.Concat(device.Tags);
        return fields.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>A device card: system logo and name, a More menu for managing it, then rows of session buttons.</summary>
    private static FrameworkElement BuildTile(FrameworkElement icon, string name, string tooltip, bool busy, IEnumerable<FrameworkElement> body, Action<MenuFlyout> more)
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
    private static FrameworkElement MachineIcon(string? logo, string fallbackGlyph, string systemTooltip, string? countryCode, string locationTooltip)
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

    private FrameworkElement BuildLocalCard()
    {
        var actions = new WrapPanel();
        var unavailable = new List<string>();
        var messages = new List<string>();
        foreach (var kind in new[] { LocalKind.Shell, LocalKind.Wsl, LocalKind.Docker, LocalKind.Multipass })
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

        var body = new List<FrameworkElement> { actions };
        bool probing = _app.ProbingDevices.Contains(device.Id);

        var lines = new List<string> { device.Name, DescribeEndpoint(device), DescribeAuth(device) + " · " + DescribeRoute(device.Network) };
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

        return BuildTile(icon, device.Name, string.Join("\n", lines), probing, body, menu =>
        {
            menu.Items.Add(Ui.MenuItem("添加连接", Ui.GlyphAdd, () => _ = AddConnectionAsync(device)));
            if (SupportsSsh(device))
            {
                var probe = Ui.MenuItem(probing ? "正在扫描…" : "扫描系统、软件和地区", Ui.GlyphRefresh, () => _ = ProbeAsync(device));
                probe.IsEnabled = !probing;
                menu.Items.Add(probe);
            }
            menu.Items.Add(Ui.MenuItem("编辑设备", Ui.GlyphEdit, () => _ = EditDeviceAsync(device)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.MenuItem("删除设备", Ui.GlyphDelete, () => _ = DeleteDeviceAsync(device)));
        });
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

    public async Task AddDeviceAsync()
    {
        if (await DeviceDialog.ShowAsync(null) is not { } device) return;
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
