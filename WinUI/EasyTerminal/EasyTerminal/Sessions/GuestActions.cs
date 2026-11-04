using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Sessions;

/// <summary>
/// What can be done with a Proxmox guest: get in, start and stop it. Shared by the host's Proxmox tab
/// and the discovered section of the devices page, which may show guests from the last saved scan.
/// </summary>
public static class GuestActions
{
    private static AppServices App => AppServices.Current;

    public static string Glyph(PveGuest guest) => guest.Kind == PveGuestKind.Lxc ? Ui.GlyphContainer : Ui.GlyphVm;

    public static string DescribeStatus(PveGuest guest) =>
        guest.IsRunning ? "运行中" : guest.Status == "stopped" ? "已停止" : guest.Status;

    /// <summary>The main way in: a shell for a container, the web console for a VM.</summary>
    public static void Enter(SessionManager sessions, Device host, PveGuest guest)
    {
        if (guest.Kind == PveGuestKind.Lxc) _ = sessions.OpenGuestAsync(host, guest);
        else OpenWebConsole(sessions, host, guest);
    }

    public static void AddMenuItems(IList<MenuFlyoutItemBase> items, SessionManager sessions, Device host, PveGuest guest)
    {
        if (guest.Kind == PveGuestKind.Qemu)
        {
            items.Add(Ui.MenuItem("网页控制台", Ui.GlyphGlobe, () => OpenWebConsole(sessions, host, guest)));
            items.Add(Ui.MenuItem("串口控制台（qm terminal）", Ui.GlyphTerminal, () => _ = sessions.OpenGuestAsync(host, guest)));
            items.Add(Ui.MenuItem("通过 SSH 连接系统内部", Ui.GlyphTerminal, () => _ = OpenThroughHostAsync(sessions, host, guest, ConnectionProtocol.Ssh)));
            items.Add(Ui.MenuItem("通过 RDP 连接系统内部", Ui.GlyphRemoteDesktop, () => _ = OpenThroughHostAsync(sessions, host, guest, ConnectionProtocol.Rdp)));
        }
        else
        {
            items.Add(Ui.MenuItem("进入容器", Ui.GlyphPlay, () => _ = sessions.OpenGuestAsync(host, guest)));
            items.Add(Ui.MenuItem("网页控制台", Ui.GlyphGlobe, () => OpenWebConsole(sessions, host, guest)));
        }
        items.Add(new MenuFlyoutSeparator());
        if (guest.IsRunning)
        {
            items.Add(Ui.MenuItem("重启", Ui.GlyphRefresh, () => _ = RunAsync(host, guest, "reboot", "重启")));
            items.Add(Ui.MenuItem("关机", Ui.GlyphStop, () => _ = RunAsync(host, guest, "shutdown", "关机")));
            items.Add(Ui.MenuItem("强制停止", Ui.GlyphStop, () => _ = RunAsync(host, guest, "stop", "强制停止")));
        }
        else
        {
            items.Add(Ui.MenuItem("启动", Ui.GlyphPlay, () => _ = RunAsync(host, guest, "start", "启动")));
        }
    }

    public static void OpenWebConsole(SessionManager sessions, Device host, PveGuest guest)
    {
        string address = HostEndpoint.Parse(host.Host).Host;
        sessions.OpenWeb($"{guest.Name} 控制台", ProxmoxCommands.WebConsoleUrl(address.Contains(':') ? $"[{address}]" : address, App.ProxmoxNode(host), guest), host);
    }

    public static async Task RunAsync(Device host, PveGuest guest, string action, string label)
    {
        if (action != "start" && !await Dialogs.ConfirmAsync($"{label} {guest.Name}？", action == "stop" ? "相当于直接断电，未保存的数据会丢失。" : "其中运行的程序会结束。", label, destructive: true)) return;
        try
        {
            await App.RunRemoteActionAsync(host, ProxmoxCommands.Action(guest, action, App.ProxmoxNeedsSudo(host)), TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync($"无法{label} {guest.Name}", ex.Message);
        }
    }

    /// <summary>The device a guest was saved as by connecting to it through its host, if any.</summary>
    public static Device? SavedDevice(Device host, PveGuest guest) =>
        App.Store.Data.Devices.FirstOrDefault(d => d.Name == guest.Name && d.Network.JumpDeviceIds.Contains(host.Id));

    /// <summary>Saves the guest as a device behind this host, so the host can serve as its jump server.</summary>
    public static async Task OpenThroughHostAsync(SessionManager sessions, Device host, PveGuest guest, ConnectionProtocol protocol)
    {
        var existing = SavedDevice(host, guest);
        var saved = existing?.Connections.FirstOrDefault(c => c.Protocol == protocol);
        if (existing != null && saved != null)
        {
            await sessions.OpenConnectionAsync(existing, saved);
            return;
        }

        string? address = existing?.Host;
        if (address == null)
        {
            try
            {
                address = await App.GetVmAddressAsync(host, guest);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                address = null;
            }
        }
        var ip = new TextBox { Header = "虚拟机地址", Text = address ?? "", PlaceholderText = "未能通过 QEMU Guest Agent 获取，请手动填写" };
        var user = new TextBox { Header = "用户名", Text = existing?.User ?? "" };
        var panel = new StackPanel { Spacing = 8, MinWidth = 400 };
        panel.Children.Add(ip);
        panel.Children.Add(user);
        panel.Children.Add(Ui.Secondary($"连接会经 {host.Name} 跳转，并保存为一台设备。", true));
        var dialog = new ContentDialog { Title = $"连接到 {guest.Name}", Content = panel, PrimaryButtonText = "连接", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(ip.Text)) return;

        var target = existing;
        if (target == null)
        {
            // Guests land in a folder named after their host, next to wherever the host itself is filed.
            target = new Device { Name = guest.Name, Host = ip.Text.Trim(), User = user.Text.Trim(), Group = FolderPath.Combine(host.Group, host.Name) };
            target.Tags.Add(guest.Kind == PveGuestKind.Lxc ? "lxc" : "vm");
            target.Network.JumpDeviceIds.Add(host.Id);
            App.Store.Data.Devices.Add(target);
        }
        else
        {
            target.Host = ip.Text.Trim();
            target.User = user.Text.Trim();
        }
        var connection = new Connection { Name = protocol == ConnectionProtocol.Rdp ? "远程桌面" : "SSH", Protocol = protocol };
        target.Connections.Add(connection);
        if (protocol == ConnectionProtocol.Ssh) target.OfferBuiltInSessions();
        App.NotifyDevicesChanged();
        await sessions.OpenConnectionAsync(target, connection);
    }
}
