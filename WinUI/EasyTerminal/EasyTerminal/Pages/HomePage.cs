using EasyTerminal.Core.Models;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages;

/// <summary>An overview of running sessions and the local environment. New sessions start from the devices page.</summary>
public sealed class HomePage : UserControl, IRefreshablePage
{
    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly StackPanel _sessionList = new() { Spacing = 6 };
    private readonly Grid _environment = new() { ColumnSpacing = 8 };

    public HomePage(SessionManager sessions)
    {
        _sessions = sessions;
        var (root, content) = Ui.Page();
        Content = root;

        content.Children.Add(Ui.Title("主页"));
        content.Children.Add(Ui.Section("正在运行的会话"));
        content.Children.Add(_sessionList);

        var header = Ui.Columns("*", "auto");
        header.Children.Add(Ui.Section("本机环境").InColumn(0));
        header.Children.Add(Ui.Button("重新扫描", Ui.GlyphRefresh, () => _ = _app.ScanLocalAsync()).InColumn(1));
        content.Children.Add(header);
        for (int i = 0; i < 3; i++) _environment.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(_environment);

        _app.ScanChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        RefreshSessions();
        RefreshEnvironment();
    }

    private void RefreshSessions()
    {
        _sessionList.Children.Clear();
        if (_sessions.Tabs.Count == 0)
        {
            var empty = Ui.Columns("*", "auto");
            var message = Ui.Secondary("没有运行中的会话。请到设备页选择一台机器新建。");
            message.VerticalAlignment = VerticalAlignment.Center;
            empty.Children.Add(message.InColumn(0));
            empty.Children.Add(Ui.Button("前往设备", Ui.GlyphDevices, () => App.MainWindowInstance?.Navigate("devices"), accent: true).InColumn(1));
            _sessionList.Children.Add(Ui.Card(empty));
            return;
        }

        foreach (var tab in _sessions.Tabs)
        {
            var row = Ui.Columns("auto", "*", "auto", "auto", "auto");
            row.Children.Add(Ui.Icon(tab.Glyph).InColumn(0));
            var text = new StackPanel();
            text.Children.Add(Ui.Body(tab.Title));
            string origin = tab.Device?.Name ?? (tab.LocalTarget != null ? "本机 · " + Ui.Name(tab.LocalTarget.Kind) : "");
            text.Children.Add(Ui.Secondary($"{origin} · {tab.StatusText} · 启动于 {Ui.RelativeTime(tab.OpenedAt)}"));
            row.Children.Add(text.InColumn(1));
            var captured = tab;
            row.Children.Add(Ui.IconButton(Ui.GlyphPlay, "切换到此会话", () => ActivateTab(captured)).InColumn(2));
            row.Children.Add(Ui.IconButton(Ui.GlyphRefresh, "重新连接", () => { captured.Restart(); ActivateTab(captured); }).InColumn(3));
            row.Children.Add(Ui.IconButton(Ui.GlyphClose, "关闭", () => _ = CloseAsync(captured)).InColumn(4));
            _sessionList.Children.Add(Ui.Card(row, 10));
        }
    }

    private static void ActivateTab(SessionTab tab) => App.MainWindowInstance?.ActivateTab(tab);

    private async Task CloseAsync(SessionTab tab)
    {
        if (!tab.HasForegroundWork || await Dialogs.ConfirmAsync($"关闭 {tab.Title}？", "连接或进程会被终止。", "关闭", destructive: true))
            _sessions.Close(tab);
    }

    private void RefreshEnvironment()
    {
        _environment.Children.Clear();
        int column = 0;
        foreach (var kind in new[] { LocalKind.Wsl, LocalKind.Docker, LocalKind.Multipass })
        {
            var status = _app.Scan?.Statuses.FirstOrDefault(s => s.Kind == kind);
            var panel = new StackPanel { Spacing = 4 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(Ui.Icon(Ui.GlyphFor(kind)));
            title.Children.Add(new TextBlock { Text = Ui.Name(kind), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            panel.Children.Add(title);
            panel.Children.Add(Ui.Secondary(_app.IsScanning ? "正在扫描…" : status?.Message ?? "尚未扫描", true));
            _environment.Children.Add(Ui.Card(panel).InColumn(column++));
        }
    }
}
