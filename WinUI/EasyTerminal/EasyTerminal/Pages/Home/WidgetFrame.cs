using EasyTerminal.Core.Models;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace EasyTerminal.Pages.Home;

/// <summary>What a panel's edit controls ask the home page to do.</summary>
public interface IHomeEditor
{
    bool IsEditing { get; }
    void Remove(HomeWidget widget);
    void Resize(HomeWidget widget, int width);
    void Move(HomeWidget widget, int delta);
    void MoveNextTo(Guid dragged, HomeWidget target, bool after);
    Task ConfigureAsync(HomeWidget widget);
}

/// <summary>The card around one home panel: icon, title, the panel's own action and, while editing, the layout controls.</summary>
public sealed class WidgetFrame : Grid
{
    public static readonly (int Width, string Name)[] Widths = { (3, "1/4 宽"), (4, "1/3 宽"), (6, "半宽"), (8, "2/3 宽"), (12, "整行") };

    private static Guid? s_dragging;

    private readonly IHomeEditor _editor;
    private readonly Border _card;
    private readonly StackPanel _editTools = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly ContentPresenter _action = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title;

    public HomeWidgetView View { get; }
    public int Span => View.Model.Width;

    public WidgetFrame(HomeWidgetView view, IHomeEditor editor)
    {
        View = view;
        _editor = editor;

        var header = Ui.Columns("auto", "*", "auto", "auto");
        header.ColumnSpacing = 8;
        var icon = Ui.Icon(view.Glyph);
        icon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon.InColumn(0));
        _title = Ui.Strong("");
        _title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_title.InColumn(1));
        header.Children.Add(_action.InColumn(2));
        header.Children.Add(_editTools.InColumn(3));

        var grip = Ui.Icon("", 14);
        grip.Margin = new Thickness(4, 0, 6, 0);
        grip.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(grip, "拖动以调整顺序");
        _editTools.Children.Add(grip);
        _editTools.Children.Add(Ui.MenuButton(Ui.GlyphMore, "布局", BuildLayoutMenu));
        if (view.HasOptions) _editTools.Children.Add(Ui.IconButton(Ui.GlyphEdit, "组件设置", () => _ = _editor.ConfigureAsync(View.Model)));
        _editTools.Children.Add(Ui.IconButton(Ui.GlyphDelete, "移除组件", () => _editor.Remove(View.Model)));

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(header);
        body.Children.Add(view.Body);
        _card = Ui.Card(body, 14);
        _card.VerticalAlignment = VerticalAlignment.Stretch;
        _card.BorderThickness = new Thickness(1);
        Children.Add(_card);

        DragStarting += (_, args) =>
        {
            s_dragging = View.Model.Id;
            args.Data.SetText(View.Model.Id.ToString());
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        DropCompleted += (_, _) => s_dragging = null;
        DragOver += (_, args) =>
        {
            if (!_editor.IsEditing || s_dragging is not { } id || id == View.Model.Id) return;
            args.AcceptedOperation = DataPackageOperation.Move;
            args.DragUIOverride.IsCaptionVisible = false;
            args.DragUIOverride.IsGlyphVisible = false;
            Highlight(true);
        };
        DragLeave += (_, _) => Highlight(false);
        Drop += (_, args) =>
        {
            Highlight(false);
            if (s_dragging is not { } id || id == View.Model.Id) return;
            bool after = args.GetPosition(this).X > ActualWidth / 2;
            s_dragging = null;
            _editor.MoveNextTo(id, View.Model, after);
        };

        UpdateChrome();
    }

    private Brush? _normalBorder;

    private void Highlight(bool on)
    {
        _normalBorder ??= _card.BorderBrush;
        _card.BorderBrush = on ? Ui.Brush("AccentFillColorDefaultBrush") : _normalBorder;
    }

    private void BuildLayoutMenu(MenuFlyout menu)
    {
        foreach (var (width, name) in Widths)
        {
            var item = new ToggleMenuFlyoutItem { Text = name, IsChecked = View.Model.Width == width };
            item.Click += (_, _) => _editor.Resize(View.Model, width);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.MenuItem("前移", Ui.GlyphUp, () => _editor.Move(View.Model, -1)));
        menu.Items.Add(Ui.MenuItem("后移", Ui.GlyphDownload, () => _editor.Move(View.Model, 1)));
    }

    /// <summary>Shows the edit controls or the panel's own action, and the current title.</summary>
    public void UpdateChrome()
    {
        bool editing = _editor.IsEditing;
        _title.Text = View.Title;
        _editTools.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _action.Content = editing ? null : View.HeaderAction;
        CanDrag = editing;
        AllowDrop = editing;
    }
}
