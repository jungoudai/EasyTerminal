using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages;

/// <summary>Saved SSH tunnels: local, remote and dynamic (SOCKS5). Each runs as its own background ssh.</summary>
public sealed class PortForwardsPage : UserControl, IRefreshablePage
{
    private readonly AppServices _app = AppServices.Current;
    private readonly StackPanel _list = new() { Spacing = 8 };

    public PortForwardsPage()
    {
        var (root, content) = Ui.Page();
        Content = root;
        var header = Ui.Columns("*", "auto");
        header.Children.Add(Ui.Title("端口转发").InColumn(0));
        header.Children.Add(Ui.Button("新建规则", Ui.GlyphAdd, () => _ = AddAsync(), accent: true).InColumn(1));
        content.Children.Add(header);
        content.Children.Add(_list);
        _app.ForwardsChanged += Refresh;
    }

    public void Refresh()
    {
        _list.Children.Clear();
        if (_app.Store.Data.PortForwards.Count == 0) _list.Children.Add(Ui.Secondary("还没有规则。"));
        foreach (var rule in _app.Store.Data.PortForwards) _list.Children.Add(BuildRow(rule));
    }

    private FrameworkElement BuildRow(PortForward rule)
    {
        _app.Forwards.TryGetValue(rule.Id, out var state);
        bool running = _app.IsForwardRunning(rule);
        var device = _app.Store.FindDevice(rule.DeviceId);

        var row = Ui.Columns("auto", "*", "auto", "auto", "auto");
        row.Children.Add(Ui.Icon(Ui.GlyphForward).InColumn(0));
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Ui.Strong(rule.Name.Length > 0 ? rule.Name : PortForwardArguments.Describe(rule)));
        text.Children.Add(Ui.Secondary($"{PortForwardArguments.Describe(rule)} · 经 {device?.Name ?? "（已删除的设备）"}" + (rule.AutoStart ? " · 启动时开启" : "")));
        string status = state?.IsStarting == true ? "正在连接" : state?.Tunnel != null ? "运行中" : state?.Error is { } error ? "已停止：" + error : "未运行";
        text.Children.Add(Ui.Secondary(status, true));
        row.Children.Add(text.InColumn(1));

        var toggle = new ToggleSwitch { IsOn = running, OnContent = "开", OffContent = "关", MinWidth = 0 };
        ToolTipService.SetToolTip(toggle, running ? "停止" : "开始");
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn == _app.IsForwardRunning(rule)) return;
            if (toggle.IsOn) _ = _app.StartForwardAsync(rule);
            else _app.StopForward(rule);
        };
        row.Children.Add(toggle.InColumn(2));
        row.Children.Add(Ui.IconButton(Ui.GlyphEdit, "编辑", () => _ = EditAsync(rule)).InColumn(3));
        row.Children.Add(Ui.IconButton(Ui.GlyphDelete, "删除", () => _ = DeleteAsync(rule)).InColumn(4));
        return Ui.Card(row, 10);
    }

    private async Task AddAsync()
    {
        var rule = new PortForward();
        if (!await EditDialogAsync(rule, true)) return;
        _app.Store.Data.PortForwards.Add(rule);
        _app.Store.Save();
        Refresh();
    }

    private async Task EditAsync(PortForward rule)
    {
        if (!await EditDialogAsync(rule, false)) return;
        _app.Store.Save();
        // A running tunnel still has the old settings, so it restarts on the new ones.
        if (_app.IsForwardRunning(rule))
        {
            _app.StopForward(rule);
            _ = _app.StartForwardAsync(rule);
        }
        Refresh();
    }

    private async Task DeleteAsync(PortForward rule)
    {
        if (!await Dialogs.ConfirmAsync("删除这条规则？", PortForwardArguments.Describe(rule), "删除", destructive: true)) return;
        _app.Store.Data.PortForwards.Remove(rule);
        _app.StopForward(rule);
        _app.Store.Save();
        Refresh();
    }

    private async Task<bool> EditDialogAsync(PortForward rule, bool isNew)
    {
        var devices = _app.Store.Data.Devices.Where(DevicesPage.SupportsSsh).ToList();
        if (devices.Count == 0)
        {
            await Dialogs.AlertAsync("没有可用设备", "端口转发需要一台支持 SSH 的设备。");
            return false;
        }

        var name = new TextBox { Header = "名称（可选）", Text = rule.Name };
        var kind = new ComboBox { Header = "类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        kind.Items.Add(new ComboBoxItem { Content = "本地转发：本机端口访问远程服务", Tag = PortForwardKind.Local });
        kind.Items.Add(new ComboBoxItem { Content = "远程转发：远程端口访问本机服务", Tag = PortForwardKind.Remote });
        kind.Items.Add(new ComboBoxItem { Content = "动态转发：本机 SOCKS5 代理", Tag = PortForwardKind.Dynamic });
        kind.SelectedIndex = (int)rule.Kind;
        var device = new ComboBox { Header = "经由设备", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var candidate in devices)
        {
            var item = new ComboBoxItem { Content = $"{candidate.Name}（{candidate.Host}）", Tag = candidate };
            device.Items.Add(item);
            if (candidate.Id == rule.DeviceId) device.SelectedItem = item;
        }
        if (device.SelectedItem == null) device.SelectedIndex = 0;

        var bindAddress = new TextBox { Header = "监听地址", Text = rule.BindAddress };
        var bindPort = new NumberBox { Header = "监听端口", Minimum = 1, Maximum = 65535 };
        if (rule.BindPort > 0) bindPort.Value = rule.BindPort;
        var targetHost = new TextBox { Header = "目标地址", Text = rule.TargetHost };
        var targetPort = new NumberBox { Header = "目标端口", Minimum = 1, Maximum = 65535 };
        if (rule.TargetPort > 0) targetPort.Value = rule.TargetPort;
        var bindRow = Ui.Columns("*", "110");
        bindRow.Children.Add(bindAddress.InColumn(0));
        bindRow.Children.Add(bindPort.InColumn(1));
        var targetRow = Ui.Columns("*", "110");
        targetRow.Children.Add(targetHost.InColumn(0));
        targetRow.Children.Add(targetPort.InColumn(1));
        var autoStart = new CheckBox { Content = "应用启动时自动开启", IsChecked = rule.AutoStart };

        var targetNote = Ui.Secondary("", true);
        void UpdateFields()
        {
            var value = (PortForwardKind)((ComboBoxItem)kind.SelectedItem).Tag;
            targetRow.Visibility = value == PortForwardKind.Dynamic ? Visibility.Collapsed : Visibility.Visible;
            targetNote.Text = value switch
            {
                PortForwardKind.Local => "目标地址从设备的角度解析，127.0.0.1 表示设备本身。",
                PortForwardKind.Remote => "监听发生在设备上；目标地址从这台电脑的角度解析。对外监听需要服务器开启 GatewayPorts。",
                _ => "把程序的 SOCKS5 代理设为监听地址和端口即可。",
            };
        }
        kind.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        panel.Children.Add(name);
        panel.Children.Add(kind);
        panel.Children.Add(device);
        panel.Children.Add(bindRow);
        panel.Children.Add(targetRow);
        panel.Children.Add(targetNote);
        panel.Children.Add(autoStart);
        panel.Children.Add(error);

        PortForward Read() => new()
        {
            Kind = (PortForwardKind)((ComboBoxItem)kind.SelectedItem).Tag,
            BindAddress = bindAddress.Text.Trim(),
            BindPort = double.IsNaN(bindPort.Value) ? 0 : (int)bindPort.Value,
            TargetHost = targetHost.Text.Trim(),
            TargetPort = double.IsNaN(targetPort.Value) ? 0 : (int)targetPort.Value,
        };

        var dialog = new ContentDialog
        {
            Title = isNew ? "新建端口转发" : "编辑端口转发",
            Content = new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (PortForwardArguments.Validate(Read()) is not { } problem) return;
            error.Message = problem;
            error.IsOpen = true;
            args.Cancel = true;
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return false;

        var values = Read();
        rule.Name = name.Text.Trim();
        rule.Kind = values.Kind;
        rule.DeviceId = ((Device)((ComboBoxItem)device.SelectedItem!).Tag!).Id;
        rule.BindAddress = values.BindAddress;
        rule.BindPort = values.BindPort;
        rule.TargetHost = values.TargetHost;
        rule.TargetPort = values.TargetPort;
        rule.AutoStart = autoStart.IsChecked == true;
        return true;
    }
}
