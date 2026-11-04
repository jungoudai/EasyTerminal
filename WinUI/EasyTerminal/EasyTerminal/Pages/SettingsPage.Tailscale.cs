using EasyTerminal.Core.Models;
using EasyTerminal.Core.Tailscale;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages;

/// <summary>The built-in Tailscale node: sign-in, status, the machines in the tailnet and how the node starts.</summary>
public sealed partial class SettingsPage
{
    private readonly TailscaleNode _node = TailscaleNode.Shared;
    private readonly TextBlock _tsState = Ui.Strong("");
    private readonly TextBlock _tsDetail = Ui.Secondary("", true);
    private readonly StackPanel _tsPeers = new() { Spacing = 4 };
    private readonly Button _tsStart = Ui.Button("启动", Ui.GlyphPlay, () => { }, true);
    private readonly Button _tsLogin = Ui.Button("登录", Ui.GlyphOpenExternal, () => { }, true);
    private readonly Button _tsLogout = Ui.Button("退出登录", Ui.GlyphClose, () => { });
    private readonly Button _tsStop = Ui.Button("停止", Ui.GlyphStop, () => { });
    private readonly DispatcherTimer _tsTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private FrameworkElement? _tsPeersCard;
    private TailscaleStatus? _tsStatus;
    private bool _tsBusy;

    private void AddTailscaleSettings(StackPanel content)
    {
        var ts = _app.Store.Data.Settings.Tailscale;
        content.Children.Add(Ui.Section("Tailscale（内置）"));

        if (!_node.IsHelperAvailable)
        {
            content.Children.Add(Ui.Card(Ui.Secondary($"此版本没有包含内置 Tailscale 组件（{TailscaleNode.HelperFileName}）。安装 Go 后重新构建即可生成。", true), 12));
            return;
        }

        var intro = Ui.Secondary("本应用自带一个 Tailscale 节点（tsnet），无需安装 Tailscale 客户端或管理员权限。在设备的网络设置里把代理类型选为「内置 Tailscale」，SSH、SFTP、端口转发和远程桌面就会经它连接。", true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_tsStart);
        buttons.Children.Add(_tsLogin);
        buttons.Children.Add(_tsLogout);
        buttons.Children.Add(_tsStop);
        var status = new StackPanel { Spacing = 6 };
        status.Children.Add(intro);
        status.Children.Add(_tsState);
        status.Children.Add(_tsDetail);
        status.Children.Add(buttons);
        content.Children.Add(Ui.Card(status, 12));

        _tsPeersCard = Ui.Card(_tsPeers, 12);
        content.Children.Add(_tsPeersCard);

        _tsStart.Click += (_, _) => _ = RunTailscaleAsync(() => _node.StartAsync());
        _tsStop.Click += (_, _) =>
        {
            _node.Stop();
            _tsStatus = null;
            ShowTailscale();
        };
        _tsLogin.Click += (_, _) => _ = RunTailscaleAsync(async () =>
        {
            string url = await _node.LoginInteractiveAsync();
            if (url.Length > 0) await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        });
        _tsLogout.Click += async (_, _) =>
        {
            if (!await Dialogs.ConfirmAsync("退出 Tailscale 登录？", "本机会从 tailnet 中断开，经内置 Tailscale 的连接将无法使用，直到重新登录。", "退出登录", true)) return;
            await RunTailscaleAsync(() => _node.LogoutAsync());
        };

        content.Children.Add(Row("随应用启动内置 Tailscale", Toggle(ts.StartWithApp, v => Update(s => s.Tailscale.StartWithApp = v))));

        var hostname = new TextBox { MinWidth = 260, Text = ts.Hostname, PlaceholderText = "easyterminal-" + Environment.MachineName.ToLowerInvariant() };
        hostname.LostFocus += (_, _) => Update(s => s.Tailscale.Hostname = hostname.Text.Trim());
        content.Children.Add(Row("本机在 tailnet 中的名称（重新启动后生效）", hostname));

        var control = new TextBox { MinWidth = 260, Text = ts.ControlUrl, PlaceholderText = "留空使用 Tailscale 官方" };
        control.LostFocus += (_, _) =>
        {
            string text = control.Text.Trim();
            if (text.Length == 0 || (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")) Update(s => s.Tailscale.ControlUrl = text);
            else control.Text = _app.Store.Data.Settings.Tailscale.ControlUrl;
        };
        content.Children.Add(Row("控制服务器（自建 Headscale 时填写）", control));

        var key = new PasswordBox { MinWidth = 260, PlaceholderText = ts.AuthKeySecretId != null ? "已保存（输入新的以替换）" : "tskey-auth-…（可选）" };
        key.LostFocus += (_, _) =>
        {
            if (key.Password.Length == 0) return;
            string id = _app.Store.Secrets.Set(_app.Store.Data.Settings.Tailscale.AuthKeySecretId, key.Password.Trim());
            key.Password = "";
            key.PlaceholderText = "已保存（输入新的以替换）";
            Update(s => s.Tailscale.AuthKeySecretId = id);
        };
        content.Children.Add(Row("预授权密钥（免浏览器登录，加密保存在本机）", key));

        content.Children.Add(Row("临时节点（退出应用后从 tailnet 移除）", Toggle(ts.Ephemeral, v => Update(s => s.Tailscale.Ephemeral = v))));

        _tsTimer.Tick += (_, _) => _ = RefreshTailscaleAsync();
        Loaded += (_, _) =>
        {
            _node.StateChanged += OnTailscaleChanged;
            _tsTimer.Start();
            _ = RefreshTailscaleAsync();
        };
        Unloaded += (_, _) =>
        {
            _node.StateChanged -= OnTailscaleChanged;
            _tsTimer.Stop();
        };
        ShowTailscale();
    }

    private void OnTailscaleChanged() => DispatcherQueue.TryEnqueue(() => _ = RefreshTailscaleAsync());

    private async Task RunTailscaleAsync(Func<Task> action)
    {
        if (_tsBusy) return;
        _tsBusy = true;
        ShowTailscale();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Dialogs.AlertAsync("Tailscale", ex.Message);
        }
        finally
        {
            _tsBusy = false;
        }
        await RefreshTailscaleAsync();
    }

    private async Task RefreshTailscaleAsync()
    {
        if (_node.IsStarted)
        {
            try
            {
                _tsStatus = await _node.GetStatusAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                _tsStatus = null;
            }
        }
        else
        {
            _tsStatus = null;
        }
        ShowTailscale();
    }

    private void ShowTailscale()
    {
        var status = _tsStatus;
        bool started = _node.IsStarted;
        bool starting = _node.IsStarting || (_tsBusy && !started);

        _tsState.Text = starting ? "正在启动…"
            : !started ? "未启动"
            : status == null ? "已启动"
            : status.BackendState switch
            {
                "Running" => "已连接" + (status.Tailnet.Length > 0 ? " · " + status.Tailnet : ""),
                "NeedsLogin" => "需要登录",
                "NeedsMachineAuth" => "等待管理员批准此设备",
                "Starting" => "正在连接…",
                "Stopped" => "已停止",
                var other => other,
            };

        var detail = new List<string>();
        if (status?.Self is { } self)
        {
            if (self.DnsName.Length > 0) detail.Add(self.DnsName.TrimEnd('.'));
            if (self.Addresses.Count > 0) detail.Add(string.Join("  ", self.Addresses));
        }
        if (status != null) detail.AddRange(status.Health);
        if (!started && _node.LastError is { } error) detail.Add(error);
        _tsDetail.Text = string.Join("\n", detail);
        _tsDetail.Visibility = detail.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _tsStart.Visibility = started ? Visibility.Collapsed : Visibility.Visible;
        _tsStart.IsEnabled = !starting;
        _tsStop.Visibility = started ? Visibility.Visible : Visibility.Collapsed;
        _tsLogin.Visibility = started && status?.NeedsLogin == true ? Visibility.Visible : Visibility.Collapsed;
        _tsLogout.Visibility = started && status is { NeedsLogin: false, BackendState: "Running" or "Starting" } ? Visibility.Visible : Visibility.Collapsed;
        _tsLogin.IsEnabled = _tsLogout.IsEnabled = !_tsBusy;

        ShowPeers(status);
    }

    private void ShowPeers(TailscaleStatus? status)
    {
        _tsPeers.Children.Clear();
        if (_tsPeersCard is not { } card) return;
        if (status is not { IsRunning: true })
        {
            card.Visibility = Visibility.Collapsed;
            return;
        }
        card.Visibility = Visibility.Visible;
        _tsPeers.Children.Add(Ui.Strong($"tailnet 中的设备（{status.Peers.Count(p => p.Online)}/{status.Peers.Count} 在线）"));
        if (status.Peers.Count == 0) _tsPeers.Children.Add(Ui.Secondary("没有其他设备。"));
        foreach (var peer in status.Peers)
        {
            var row = Ui.Columns("auto", "*", "auto");
            row.ColumnSpacing = 10;
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Ui.Brush(peer.Online ? "SystemFillColorSuccessBrush" : "SystemFillColorNeutralBrush"),
            };
            row.Children.Add(dot.InColumn(0));
            var text = new StackPanel();
            text.Children.Add(Ui.Body(peer.HostName));
            text.Children.Add(Ui.Secondary(string.Join(" · ", new[] { peer.BestAddress, peer.Addresses.FirstOrDefault() ?? "", peer.Os }.Where(s => s.Length > 0).Distinct())));
            row.Children.Add(text.InColumn(1));

            var existing = _app.Store.Data.Devices.FirstOrDefault(d => IsSameMachine(d, peer));
            var action = existing != null
                ? (FrameworkElement)Ui.Secondary("已在设备中：" + existing.Name)
                : Ui.Button("添加为设备", Ui.GlyphAdd, () => AddPeerAsDevice(peer));
            action.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(action.InColumn(2));
            _tsPeers.Children.Add(row);
        }
    }

    private static bool IsSameMachine(Device device, TailscalePeer peer)
    {
        string host = Core.Ssh.HostEndpoint.Parse(device.Host).Host;
        return host.Equals(peer.BestAddress, StringComparison.OrdinalIgnoreCase)
            || host.Equals(peer.DnsName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || peer.Addresses.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    private void AddPeerAsDevice(TailscalePeer peer)
    {
        bool windows = peer.Os.Equals("windows", StringComparison.OrdinalIgnoreCase);
        var device = new Device
        {
            Name = peer.HostName,
            Host = peer.BestAddress,
            Group = "Tailscale",
            Network = new NetworkProfile { ProxyKind = ProxyKind.Tailscale },
        };
        device.Connections.Add(new Connection { Name = "SSH", Protocol = ConnectionProtocol.Ssh });
        device.Connections.Add(new Connection { Name = "SFTP", Protocol = ConnectionProtocol.Sftp });
        if (windows) device.Connections.Add(new Connection { Name = "远程桌面", Protocol = ConnectionProtocol.Rdp });
        device.OfferBuiltInSessions();
        _app.Store.Data.Devices.Add(device);
        _app.NotifyDevicesChanged();
        ShowTailscale();
    }
}
