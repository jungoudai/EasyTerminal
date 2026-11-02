using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Transport;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages;

/// <summary>Proxy and jump-host fields, shared by the device dialog and per-connection overrides.</summary>
public sealed class NetworkEditor : StackPanel
{
    private readonly AppServices _app = AppServices.Current;
    private readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "代理类型" };
    private readonly TextBox _host = new() { Header = "代理地址", PlaceholderText = "127.0.0.1" };
    private readonly NumberBox _port = new() { Header = "端口", Minimum = 1, Maximum = 65535, Value = 1080 };
    private readonly TextBox _user = new() { Header = "用户名（可选）" };
    private readonly PasswordBox _password = new() { Header = "密码（可选）", PlaceholderText = "留空则保持不变" };
    private readonly StackPanel _proxyFields = new() { Spacing = 8 };
    private readonly StackPanel _jumpRows = new() { Spacing = 4 };
    private readonly ComboBox _jumpPicker = new() { PlaceholderText = "添加跳板设备", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<Guid> _jumps;
    private readonly Guid? _ownerDeviceId;
    private string? _secretId;

    public NetworkEditor(NetworkProfile profile, Guid? ownerDeviceId)
    {
        Spacing = 8;
        _ownerDeviceId = ownerDeviceId;
        _jumps = new List<Guid>(profile.JumpDeviceIds);
        _secretId = profile.ProxySecretId;

        foreach (var kind in Enum.GetValues<ProxyKind>())
            _kind.Items.Add(new ComboBoxItem { Content = kind == ProxyKind.None ? "不使用代理" : kind.ToString().ToUpperInvariant(), Tag = kind });
        _kind.SelectedIndex = (int)profile.ProxyKind;
        _kind.SelectionChanged += (_, _) => UpdateProxyVisibility();
        _host.Text = profile.ProxyHost;
        _port.Value = profile.ProxyPort;
        _user.Text = profile.ProxyUser;

        var endpoint = Ui.Columns("*", "110");
        endpoint.Children.Add(_host.InColumn(0));
        endpoint.Children.Add(_port.InColumn(1));
        _proxyFields.Children.Add(endpoint);
        var credentials = Ui.Columns("*", "*");
        credentials.Children.Add(_user.InColumn(0));
        credentials.Children.Add(_password.InColumn(1));
        _proxyFields.Children.Add(credentials);

        Children.Add(_kind);
        Children.Add(_proxyFields);
        Children.Add(Ui.Section("跳板（按连接顺序）"));
        Children.Add(_jumpRows);
        Children.Add(_jumpPicker);
        _jumpPicker.SelectionChanged += OnJumpPicked;

        UpdateProxyVisibility();
        RefreshJumps();
    }

    private void UpdateProxyVisibility() =>
        _proxyFields.Visibility = SelectedKind == ProxyKind.None ? Visibility.Collapsed : Visibility.Visible;

    private ProxyKind SelectedKind => (ProxyKind)((ComboBoxItem)_kind.SelectedItem).Tag;

    private void OnJumpPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_jumpPicker.SelectedItem is ComboBoxItem { Tag: Guid id })
        {
            _jumps.Add(id);
            RefreshJumps();
        }
    }

    private void RefreshJumps()
    {
        _jumpRows.Children.Clear();
        for (int i = 0; i < _jumps.Count; i++)
        {
            Guid id = _jumps[i];
            var row = Ui.Columns("*", "auto");
            row.Children.Add(Ui.Body($"{i + 1}. {_app.Store.FindDevice(id)?.Name ?? "（已删除的设备）"}").InColumn(0));
            row.Children.Add(Ui.IconButton(Ui.GlyphClose, "移除", () =>
            {
                _jumps.Remove(id);
                RefreshJumps();
            }).InColumn(1));
            _jumpRows.Children.Add(row);
        }

        _jumpPicker.SelectionChanged -= OnJumpPicked;
        _jumpPicker.Items.Clear();
        foreach (var device in _app.Store.Data.Devices)
        {
            if (device.Id == _ownerDeviceId || _jumps.Contains(device.Id)) continue;
            _jumpPicker.Items.Add(new ComboBoxItem { Content = $"{device.Name} ({device.Host})", Tag = device.Id });
        }
        _jumpPicker.SelectedIndex = -1;
        _jumpPicker.IsEnabled = _jumpPicker.Items.Count > 0;
        _jumpPicker.PlaceholderText = _jumpPicker.Items.Count > 0 ? "添加跳板设备" : "没有其他设备可作跳板";
        _jumpPicker.SelectionChanged += OnJumpPicked;
    }

    public string? Validate()
    {
        if (SelectedKind != ProxyKind.None && string.IsNullOrWhiteSpace(_host.Text)) return "请填写代理地址。";
        if (double.IsNaN(_port.Value)) return "请填写代理端口。";
        return null;
    }

    public void ApplyTo(NetworkProfile profile)
    {
        profile.ProxyKind = SelectedKind;
        profile.ProxyHost = _host.Text.Trim();
        profile.ProxyPort = (int)_port.Value;
        profile.ProxyUser = _user.Text.Trim();
        if (_password.Password.Length > 0) _secretId = _app.Store.Secrets.Set(_secretId, _password.Password);
        profile.ProxySecretId = _secretId;
        profile.JumpDeviceIds = new List<Guid>(_jumps);
    }
}

public static class DeviceDialog
{
    /// <summary>Edits an existing device, or creates one when <paramref name="existing"/> is null.</summary>
    public static async Task<Device?> ShowAsync(Device? existing)
    {
        var app = AppServices.Current;
        var device = existing ?? new Device();

        var name = new TextBox { Header = "名称", Text = device.Name, PlaceholderText = "例如：家里的 NAS" };
        var host = new TextBox { Header = "主机地址", Text = device.Host, PlaceholderText = "example.com 或 192.168.1.10" };
        var kind = new ComboBox { Header = "设备类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        kind.Items.Add(new ComboBoxItem { Content = "SSH 服务器（Linux / macOS / NAS）", Tag = ConnectionProtocol.Ssh });
        kind.Items.Add(new ComboBoxItem { Content = "Windows 远程桌面（RDP）", Tag = ConnectionProtocol.Rdp });
        kind.Items.Add(new ComboBoxItem { Content = "Telnet 设备", Tag = ConnectionProtocol.Telnet });
        kind.Items.Add(new ComboBoxItem { Content = "串口设备（主机地址填 COM3 这样的端口名）", Tag = ConnectionProtocol.Serial });
        kind.SelectedIndex = 0;
        var group = new TextBox { Header = "分组（可选）", Text = device.Group, PlaceholderText = "例如：生产环境" };
        var tags = new TextBox { Header = "标签（可选，用逗号分隔）", Text = string.Join(", ", device.Tags), PlaceholderText = "例如：db, 上海" };
        var forwardAgent = new CheckBox { Content = "转发 SSH Agent（只在信任此主机时开启）", IsChecked = device.ForwardAgent };
        var alternates = new TextBox
        {
            Header = "备用地址（可选，每行一个，可带端口）",
            Text = string.Join("\r", device.AlternateAddresses),
            PlaceholderText = "home.example.com\rexample.com:2222",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 64,
        };
        var port = new NumberBox { Header = "端口", Minimum = 1, Maximum = 65535, Value = device.Port };
        var user = new TextBox { Header = "用户名", Text = device.User };
        var auth = new ComboBox { Header = "认证方式", HorizontalAlignment = HorizontalAlignment.Stretch };
        auth.Items.Add(new ComboBoxItem { Content = "SSH Agent / 默认密钥", Tag = AuthMethod.Agent });
        auth.Items.Add(new ComboBoxItem { Content = "指定密钥文件（支持硬件安全密钥）", Tag = AuthMethod.KeyFile });
        auth.Items.Add(new ComboBoxItem { Content = "密码", Tag = AuthMethod.Password });
        auth.SelectedIndex = (int)device.Auth;

        var keyPicker = new ComboBox { Header = "密钥", HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true, Text = device.KeyPath, PlaceholderText = "选择或输入私钥路径" };
        foreach (var key in await KeyManager.ListAsync())
            keyPicker.Items.Add(key.Path);
        if (device.KeyPath.Length > 0) keyPicker.SelectedItem = keyPicker.Items.Contains(device.KeyPath) ? device.KeyPath : null;

        var password = new PasswordBox { Header = "密码（使用 Windows 数据保护加密保存）", PlaceholderText = device.PasswordSecretId != null ? "留空则保持不变" : "留空则每次连接时询问" };

        void UpdateAuthFields()
        {
            var method = (AuthMethod)((ComboBoxItem)auth.SelectedItem).Tag;
            keyPicker.Visibility = method == AuthMethod.KeyFile ? Visibility.Visible : Visibility.Collapsed;
            password.Visibility = method == AuthMethod.Password ? Visibility.Visible : Visibility.Collapsed;
        }
        auth.SelectionChanged += (_, _) => UpdateAuthFields();
        UpdateAuthFields();

        var network = new NetworkEditor(device.Network, existing?.Id);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };

        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        if (existing == null) panel.Children.Add(kind);
        panel.Children.Add(name);
        var endpoint = Ui.Columns("*", "110");
        endpoint.Children.Add(host.InColumn(0));
        endpoint.Children.Add(port.InColumn(1));
        panel.Children.Add(endpoint);
        panel.Children.Add(alternates);
        panel.Children.Add(Ui.Secondary("连接时按顺序尝试。内网地址不在当前网络时会立即跳过，其余地址验证到 SSH 服务后才使用。", true));
        var organize = Ui.Columns("*", "*");
        organize.Children.Add(group.InColumn(0));
        organize.Children.Add(tags.InColumn(1));
        panel.Children.Add(organize);
        panel.Children.Add(user);
        panel.Children.Add(auth);
        panel.Children.Add(keyPicker);
        panel.Children.Add(password);
        panel.Children.Add(forwardAgent);
        panel.Children.Add(Ui.Section("网络"));
        panel.Children.Add(Ui.Secondary("默认应用到此设备的所有连接，单个连接可以单独覆盖。", true));
        panel.Children.Add(network);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = existing == null ? "添加设备" : "编辑设备",
            Content = new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? problem = string.IsNullOrWhiteSpace(host.Text) ? "请填写主机地址。" : host.Text.Trim().Contains(' ') ? "主机地址不能包含空格。" : alternates.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(a => a.Contains(' ')) ? "备用地址不能包含空格。" : network.Validate();
            if (problem != null)
            {
                error.Message = problem;
                error.IsOpen = true;
                args.Cancel = true;
            }
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return null;

        device.Host = host.Text.Trim();
        device.AlternateAddresses = alternates.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        device.Name = string.IsNullOrWhiteSpace(name.Text) ? device.Host : name.Text.Trim();
        device.Port = double.IsNaN(port.Value) ? 22 : (int)port.Value;
        device.User = user.Text.Trim();
        device.Auth = (AuthMethod)((ComboBoxItem)auth.SelectedItem).Tag;
        device.KeyPath = keyPicker.Text?.Trim() ?? "";
        if (password.Password.Length > 0) device.PasswordSecretId = app.Store.Secrets.Set(device.PasswordSecretId, password.Password);
        network.ApplyTo(device.Network);
        device.Group = group.Text.Trim();
        device.Tags = tags.Text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        device.ForwardAgent = forwardAgent.IsChecked == true;
        if (existing == null)
        {
            // What nearly every device of this kind offers, so a new one is usable right away.
            switch ((ConnectionProtocol)((ComboBoxItem)kind.SelectedItem).Tag)
            {
                case ConnectionProtocol.Rdp:
                    device.Connections.Add(new Connection { Name = "远程桌面", Protocol = ConnectionProtocol.Rdp });
                    break;
                case ConnectionProtocol.Telnet:
                    device.Connections.Add(new Connection { Name = "Telnet", Protocol = ConnectionProtocol.Telnet });
                    break;
                case ConnectionProtocol.Serial:
                    device.Connections.Add(new Connection { Name = "串口", Protocol = ConnectionProtocol.Serial });
                    break;
                default:
                    device.Connections.Add(new Connection { Name = "SSH", Protocol = ConnectionProtocol.Ssh });
                    device.Connections.Add(new Connection { Name = "SFTP", Protocol = ConnectionProtocol.Sftp });
                    device.OfferBuiltInSessions();
                    break;
            }
        }
        return device;
    }
}

public static class ConnectionDialog
{
    public static async Task<Connection?> ShowAsync(Device device, Connection? existing)
    {
        var connection = existing ?? new Connection();

        var protocol = new ComboBox { Header = "协议", HorizontalAlignment = HorizontalAlignment.Stretch };
        // Tools and network are parts of the status view now, not something to add on their own.
        foreach (var value in Enum.GetValues<ConnectionProtocol>().Where(p => p is not (ConnectionProtocol.Tools or ConnectionProtocol.Network)))
            protocol.Items.Add(new ComboBoxItem { Content = Ui.Name(value), Tag = value });
        protocol.SelectedItem = protocol.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (ConnectionProtocol)i.Tag == connection.Protocol) ?? protocol.Items[0];

        var name = new TextBox { Header = "名称", Text = connection.Name };
        var sessionName = new TextBox { Header = "会话名称", Text = connection.SessionName, PlaceholderText = "留空使用默认会话；不存在时自动创建" };
        var tmuxNote = Ui.Secondary("留空时打开 tmux 会话列表，可以 Attach 和管理；填写后直接 Attach 到这个会话。", true);
        var command = new TextBox { Text = connection.Command };
        var user = new TextBox { Header = "用户名（留空使用设备设置）", Text = connection.User };
        var port = new NumberBox { Header = "端口", Minimum = 1, Maximum = 65535, PlaceholderText = device.Port.ToString() };
        if (connection.Port is { } existingPort) port.Value = existingPort;
        var endpoint = Ui.Columns("*", "110");
        endpoint.Children.Add(user.InColumn(0));
        endpoint.Children.Add(port.InColumn(1));

        var domain = new TextBox { Header = "域（可选）", Text = connection.Domain };
        var rdpPassword = new PasswordBox { Header = "密码（使用 Windows 数据保护加密保存）", PlaceholderText = connection.PasswordSecretId != null ? "留空则保持不变" : "留空则连接时询问" };
        var clipboard = new CheckBox { Content = "共享剪贴板", IsChecked = connection.RedirectClipboard };
        var drives = new CheckBox { Content = "共享本机磁盘（对方可读写你的文件）", IsChecked = connection.RedirectDrives };
        var rdpFields = new StackPanel { Spacing = 8 };
        rdpFields.Children.Add(domain);
        rdpFields.Children.Add(rdpPassword);
        rdpFields.Children.Add(clipboard);
        rdpFields.Children.Add(drives);

        var baud = new ComboBox { Header = "波特率", HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true, Text = connection.BaudRate.ToString() };
        foreach (var rate in new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }) baud.Items.Add(rate.ToString());
        var ports = SerialTransport.AvailablePorts();
        var serialNote = Ui.Secondary(ports.Length > 0 ? $"此电脑的串口：{string.Join("、", ports)}。端口名是设备的主机地址（当前为 {device.Host}）。" : "此电脑上没有发现串口。端口名是设备的主机地址。", true);

        var startup = new ComboBox { Header = "连接后运行的片段（可选）", HorizontalAlignment = HorizontalAlignment.Stretch };
        startup.Items.Add(new ComboBoxItem { Content = "不运行" });
        foreach (var snippet in AppServices.Current.Store.Data.Snippets) startup.Items.Add(new ComboBoxItem { Content = snippet.Name, Tag = snippet.Id });
        startup.SelectedItem = startup.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is Guid id && id == connection.StartupSnippetId) ?? startup.Items[0];

        var overrideNetwork = new ToggleSwitch { Header = "单独配置此连接的代理和跳板", IsOn = connection.OverrideNetwork };
        var network = new NetworkEditor(connection.Network, device.Id);
        network.Visibility = connection.OverrideNetwork ? Visibility.Visible : Visibility.Collapsed;

        var moshNote = Ui.Secondary("Mosh 仅用代理和跳板完成 SSH 启动；之后的 UDP 流量会直连主机。", true);
        var rdpNote = Ui.Secondary("RDP 经跳板时通过 SSH 隧道转发；不支持只配代理。", true);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };

        void UpdateProtocolFields()
        {
            var value = (ConnectionProtocol)((ComboBoxItem)protocol.SelectedItem).Tag;
            Visibility When(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
            sessionName.Visibility = When(value is ConnectionProtocol.Tmux or ConnectionProtocol.Herdr);
            sessionName.PlaceholderText = value == ConnectionProtocol.Tmux ? "留空打开会话列表" : "留空使用默认会话；不存在时自动创建";
            tmuxNote.Visibility = When(value == ConnectionProtocol.Tmux);
            command.Visibility = When(value is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp);
            command.Header = value == ConnectionProtocol.Sftp ? "起始目录（可选）" : "远程命令（可选）";
            startup.Visibility = When(value == ConnectionProtocol.Ssh && AppServices.Current.Store.Data.Snippets.Count > 0);
            rdpFields.Visibility = When(value == ConnectionProtocol.Rdp);
            baud.Visibility = serialNote.Visibility = When(value == ConnectionProtocol.Serial);
            // These lists and charts use the device's own login, as does a tmux list with no session named.
            bool usesDeviceLogin = value is ConnectionProtocol.Docker or ConnectionProtocol.Proxmox or ConnectionProtocol.Monitor or ConnectionProtocol.Tools or ConnectionProtocol.Network || (value == ConnectionProtocol.Tmux && sessionName.Text.Trim().Length == 0);
            endpoint.Visibility = When(value != ConnectionProtocol.Serial && !usesDeviceLogin);
            port.Visibility = When(value != ConnectionProtocol.Serial);
            port.PlaceholderText = value switch
            {
                ConnectionProtocol.Rdp => "3389",
                ConnectionProtocol.Telnet => "23",
                _ => device.Port.ToString(),
            };
            bool routed = value is not (ConnectionProtocol.Telnet or ConnectionProtocol.Serial) && !usesDeviceLogin;
            overrideNetwork.Visibility = When(routed);
            network.Visibility = When(routed && overrideNetwork.IsOn);
            moshNote.Visibility = When(value == ConnectionProtocol.Mosh);
            rdpNote.Visibility = When(value == ConnectionProtocol.Rdp);
        }
        protocol.SelectionChanged += (_, _) => UpdateProtocolFields();
        sessionName.TextChanged += (_, _) => UpdateProtocolFields();
        overrideNetwork.Toggled += (_, _) => UpdateProtocolFields();
        UpdateProtocolFields();

        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        panel.Children.Add(protocol);
        panel.Children.Add(name);
        panel.Children.Add(sessionName);
        panel.Children.Add(tmuxNote);
        panel.Children.Add(command);
        panel.Children.Add(startup);
        panel.Children.Add(endpoint);
        panel.Children.Add(rdpFields);
        panel.Children.Add(baud);
        panel.Children.Add(serialNote);
        panel.Children.Add(overrideNetwork);
        panel.Children.Add(network);
        panel.Children.Add(moshNote);
        panel.Children.Add(rdpNote);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = existing == null ? $"为 {device.Name} 添加连接" : "编辑连接",
            Content = new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (overrideNetwork.IsOn && network.Validate() is { } problem)
            {
                error.Message = problem;
                error.IsOpen = true;
                args.Cancel = true;
            }
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return null;

        connection.Protocol = (ConnectionProtocol)((ComboBoxItem)protocol.SelectedItem).Tag;
        connection.SessionName = sessionName.Text.Trim();
        connection.Command = command.Text.Trim();
        connection.User = user.Text.Trim();
        connection.Port = double.IsNaN(port.Value) ? null : (int)port.Value;
        connection.OverrideNetwork = overrideNetwork.IsOn;
        if (overrideNetwork.IsOn) network.ApplyTo(connection.Network);
        connection.Domain = domain.Text.Trim();
        if (rdpPassword.Password.Length > 0) connection.PasswordSecretId = AppServices.Current.Store.Secrets.Set(connection.PasswordSecretId, rdpPassword.Password);
        connection.RedirectClipboard = clipboard.IsChecked == true;
        connection.RedirectDrives = drives.IsChecked == true;
        if (int.TryParse(baud.Text, out int baudRate) && baudRate > 0) connection.BaudRate = baudRate;
        connection.StartupSnippetId = (startup.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        connection.Name = string.IsNullOrWhiteSpace(name.Text)
            ? (connection.SessionName.Length > 0 ? connection.SessionName : Ui.Name(connection.Protocol))
            : name.Text.Trim();
        return connection;
    }
}
