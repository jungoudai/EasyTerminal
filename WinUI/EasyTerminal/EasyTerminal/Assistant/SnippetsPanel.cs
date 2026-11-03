using EasyTerminal.Core.Models;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EasyTerminal.Assistant;

/// <summary>Snippets beside the terminal: click one to send it to the session in front.</summary>
public sealed class SnippetsPanel : Grid
{
    private readonly AppServices _app = AppServices.Current;
    private readonly Func<SessionTab?> _target;
    private readonly Action<SessionTab> _activate;
    private readonly TextBox _search = new() { PlaceholderText = "搜索片段" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
    private readonly TextBlock _hint = Ui.Secondary("", true);
    private static readonly FontFamily MonoFont = new("Cascadia Mono");

    public SnippetsPanel(Func<SessionTab?> target, Action<SessionTab> activate, Action addSnippet, Action manageSnippets)
    {
        _target = target;
        _activate = activate;
        RowSpacing = 8;
        Padding = new Thickness(10, 0, 10, 10);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Children.Add(_search);
        SetRow(_hint, 1);
        Children.Add(_hint);
        SetRow(_list, 2);
        Children.Add(_list);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(Ui.Button("新建片段", Ui.GlyphAdd, addSnippet));
        footer.Children.Add(Ui.Button("管理片段", Ui.GlyphSnippet, manageSnippets));
        SetRow(footer, 3);
        Children.Add(footer);

        _search.TextChanged += (_, _) => Fill();
        _list.ItemClick += OnItemClick;
    }

    public void FocusSearch() => _search.Focus(FocusState.Programmatic);

    /// <summary>Re-reads the snippets and which terminal they would go to.</summary>
    public void Refresh()
    {
        var target = _target();
        _hint.Text = target != null ? "点击发送到 " + target.Title : "没有正在运行的终端，先打开一个再发送片段";
        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        string query = _search.Text.Trim();
        foreach (var snippet in _app.Store.Data.Snippets
            .Where(s => query.Length == 0 || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.LastUsed).ThenBy(s => s.Name))
        {
            var item = new StackPanel { Padding = new Thickness(0, 4, 0, 4), Tag = snippet };
            item.Children.Add(Ui.Body(snippet.Name));
            item.Children.Add(new TextBlock
            {
                Text = snippet.Body.ReplaceLineEndings(" ⏎ "),
                FontFamily = MonoFont,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Ui.Brush("TextFillColorSecondaryBrush"),
            });
            ToolTipService.SetToolTip(item, snippet.Description.Length > 0 ? snippet.Description + "\n\n" + snippet.Body : snippet.Body);
            _list.Items.Add(item);
        }
        if (_list.Items.Count == 0) _list.Items.Add(Ui.Secondary(_app.Store.Data.Snippets.Count == 0 ? "还没有片段。" : "没有匹配的片段。"));
    }

    private void OnItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not StackPanel { Tag: Snippet snippet }) return;
        var target = _target();
        if (target == null)
        {
            _ = Dialogs.AlertAsync("没有正在运行的终端", "先打开一个终端会话，再运行片段。");
            return;
        }
        _activate(target);
        _ = SnippetRunner.SendAsync(snippet, new[] { target });
    }
}
