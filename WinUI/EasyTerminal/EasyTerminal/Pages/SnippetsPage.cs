using EasyTerminal.Core.Models;
using EasyTerminal.Core.Snippets;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EasyTerminal.Pages;

/// <summary>Saved commands and code, sent to terminals or run on several devices at once.</summary>
public sealed class SnippetsPage : UserControl, IRefreshablePage
{
    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly TextBox _search = new() { PlaceholderText = "搜索片段" };
    private readonly StackPanel _list = new() { Spacing = 8 };

    public SnippetsPage(SessionManager sessions)
    {
        _sessions = sessions;
        var (root, content) = Ui.Page();
        Content = root;
        var header = Ui.Columns("*", "auto");
        header.Children.Add(Ui.Title("代码片段").InColumn(0));
        header.Children.Add(Ui.Button("新建片段", Ui.GlyphAdd, () => _ = AddAsync(), accent: true).InColumn(1));
        content.Children.Add(header);
        content.Children.Add(Ui.Secondary("用 {{名称}} 写占位符，运行时会询问具体内容。也可以在终端右键或命令面板（Ctrl+Shift+P）里运行。", true));
        content.Children.Add(_search);
        content.Children.Add(_list);
        _search.TextChanged += (_, _) => Refresh();
    }

    public void Refresh()
    {
        _list.Children.Clear();
        var snippets = _app.Store.Data.Snippets;
        if (snippets.Count == 0)
        {
            _list.Children.Add(Ui.Secondary("还没有片段。"));
            return;
        }
        string query = _search.Text.Trim();
        var matches = snippets.Where(s => query.Length == 0 || new[] { s.Name, s.Body, s.Group, s.Description }.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var group in matches.GroupBy(s => s.Group).OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            if (group.Key.Length > 0) _list.Children.Add(Ui.Section(group.Key));
            foreach (var snippet in group.OrderBy(s => s.Name, StringComparer.CurrentCulture)) _list.Children.Add(BuildRow(snippet));
        }
        if (matches.Count == 0) _list.Children.Add(Ui.Secondary("没有匹配的片段。"));
    }

    private FrameworkElement BuildRow(Snippet snippet)
    {
        var row = Ui.Columns("auto", "*", "auto", "auto", "auto", "auto");
        row.Children.Add(Ui.Icon(Ui.GlyphSnippet).InColumn(0));
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Ui.Strong(snippet.Name));
        if (snippet.Description.Length > 0) text.Children.Add(Ui.Secondary(snippet.Description));
        var body = new TextBlock
        {
            Text = snippet.Body.ReplaceLineEndings(" ⏎ "),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Ui.Brush("TextFillColorSecondaryBrush"),
        };
        text.Children.Add(body);
        row.Children.Add(text.InColumn(1));
        row.Children.Add(Ui.MenuButton(Ui.GlyphPlay, "运行", menu => BuildRunMenu(menu, snippet)).InColumn(2));
        row.Children.Add(Ui.IconButton(Ui.GlyphCopy, "复制内容", () =>
        {
            var package = new DataPackage();
            package.SetText(snippet.Body);
            Clipboard.SetContent(package);
        }).InColumn(3));
        row.Children.Add(Ui.IconButton(Ui.GlyphEdit, "编辑", () => _ = EditAsync(snippet)).InColumn(4));
        row.Children.Add(Ui.IconButton(Ui.GlyphDelete, "删除", () => _ = DeleteAsync(snippet)).InColumn(5));
        return Ui.Card(row, 10);
    }

    private void BuildRunMenu(MenuFlyout menu, Snippet snippet)
    {
        var terminals = _sessions.Tabs.Where(t => t.Terminal?.State == Core.Terminal.SessionState.Running).ToList();
        foreach (var tab in terminals)
            menu.Items.Add(Ui.MenuItem($"{tab.Device?.Name ?? "本机"} · {tab.Title}", tab.Glyph, () => _ = SendAndShowAsync(snippet, tab)));
        if (terminals.Count > 1)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.MenuItem($"发送到全部 {terminals.Count} 个终端", Ui.GlyphBroadcast, () => _ = SendToAllAsync(snippet, terminals)));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.MenuItem("在多台设备上后台运行…", Ui.GlyphDevices, () => _ = SnippetRunner.RunOnDevicesAsync(snippet)));
    }

    private static async Task SendAndShowAsync(Snippet snippet, SessionTab tab)
    {
        await SnippetRunner.SendAsync(snippet, new[] { tab });
        App.MainWindowInstance?.ActivateTab(tab);
    }

    private static async Task SendToAllAsync(Snippet snippet, IReadOnlyList<SessionTab> tabs)
    {
        if (await Dialogs.ConfirmAsync($"发送到全部 {tabs.Count} 个终端？", string.Join("、", tabs.Select(t => t.Title)), "发送"))
            await SnippetRunner.SendAsync(snippet, tabs);
    }

    public async Task AddAsync()
    {
        var snippet = new Snippet();
        if (await EditDialogAsync(snippet, true))
        {
            _app.Store.Data.Snippets.Add(snippet);
            _app.Store.Save();
            Refresh();
        }
    }

    private async Task EditAsync(Snippet snippet)
    {
        if (await EditDialogAsync(snippet, false))
        {
            _app.Store.Save();
            Refresh();
        }
    }

    private async Task DeleteAsync(Snippet snippet)
    {
        if (!await Dialogs.ConfirmAsync($"删除片段 {snippet.Name}？", "这个操作无法撤销。", "删除", destructive: true)) return;
        _app.Store.Data.Snippets.Remove(snippet);
        foreach (var connection in _app.Store.Data.Devices.SelectMany(d => d.Connections).Where(c => c.StartupSnippetId == snippet.Id))
            connection.StartupSnippetId = null;
        _app.Store.Save();
        Refresh();
    }

    private static async Task<bool> EditDialogAsync(Snippet snippet, bool isNew)
    {
        var name = new TextBox { Header = "名称", Text = snippet.Name };
        var body = new TextBox
        {
            Header = "内容",
            Text = snippet.Body.Replace("\r\n", "\r").Replace('\n', '\r'),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 140,
            MaxHeight = 280,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            PlaceholderText = "docker logs -f --tail 100 {{容器}}",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(body, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(body, ScrollBarVisibility.Auto);
        var group = new TextBox { Header = "分组（可选）", Text = snippet.Group };
        var description = new TextBox { Header = "说明（可选）", Text = snippet.Description };
        var run = new CheckBox { Content = "发送后按回车执行", IsChecked = snippet.RunImmediately };
        var variables = Ui.Secondary("", true);
        void UpdateVariables()
        {
            var found = SnippetTemplate.Variables(body.Text);
            variables.Text = found.Count > 0 ? "运行时询问：" + string.Join("、", found) : "可以用 {{名称}} 写占位符。";
        }
        body.TextChanged += (_, _) => UpdateVariables();
        UpdateVariables();

        var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
        panel.Children.Add(name);
        panel.Children.Add(body);
        panel.Children.Add(variables);
        var meta = Ui.Columns("*", "*");
        meta.Children.Add(group.InColumn(0));
        meta.Children.Add(description.InColumn(1));
        panel.Children.Add(meta);
        panel.Children.Add(run);

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false, Message = "请填写内容。" };
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = isNew ? "新建片段" : "编辑片段",
            Content = new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (body.Text.Trim().Length > 0) return;
            error.IsOpen = true;
            args.Cancel = true;
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return false;

        snippet.Body = body.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        string first = snippet.Body.Trim().Split('\n')[0];
        snippet.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : first[..Math.Min(first.Length, 40)];
        snippet.Group = group.Text.Trim();
        snippet.Description = description.Text.Trim();
        snippet.RunImmediately = run.IsChecked == true;
        return true;
    }
}
