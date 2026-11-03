using EasyTerminal.Controls;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EasyTerminal.Assistant;

/// <summary>
/// A panel on the right edge of the window. Pinned, it takes its width from the sessions beside it;
/// floating, it lies over them, so terminals keep their size and don't redraw or reflow.
/// </summary>
public sealed class SidePanel : Grid
{
    private readonly ToggleButton _pin = new()
    {
        Content = Ui.Icon("", 14),
        Padding = new Thickness(8, 6, 8, 6),
        BorderThickness = new Thickness(0),
    };
    private readonly ContentPresenter _body = new();
    private readonly ContentPresenter _headerExtra = new();
    private readonly TextBlock _title = Ui.Strong("");

    public string Key { get; }
    public bool IsPinned { get; private set; }
    public event Action? PinChanged;
    public event Action? CloseRequested;
    public event Action<double>? WidthDragged;
    public event Action? WidthDragEnded;

    public SidePanel(string key, string title, FrameworkElement body, bool pinned)
    {
        Key = key;
        _title.Text = title;
        _body.Content = body;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var handle = new ResizeHandle();
        handle.Dragged += delta => WidthDragged?.Invoke(delta);
        handle.DragEnded += () => WidthDragEnded?.Invoke();
        SetRowSpan(handle, 2);
        Children.Add(handle);

        var header = Ui.Columns("auto", "*", "auto", "auto");
        header.Padding = new Thickness(6, 6, 6, 4);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(4, 0, 0, 0);
        header.Children.Add(_title.InColumn(0));
        header.Children.Add(_headerExtra.InColumn(1));
        _pin.Click += (_, _) => SetPinned(_pin.IsChecked == true, raise: true);
        header.Children.Add(_pin.InColumn(2));
        header.Children.Add(Ui.IconButton(Ui.GlyphClose, "关闭", () => CloseRequested?.Invoke()).InColumn(3));
        SetColumn(header, 1);
        Children.Add(header);

        SetColumn(_body, 1);
        SetRow(_body, 1);
        Children.Add(_body);

        SetPinned(pinned, raise: false);
    }

    public FrameworkElement Body
    {
        get => (FrameworkElement)_body.Content;
        set => _body.Content = value;
    }

    /// <summary>Controls shown in the header between the title and the pin button.</summary>
    public FrameworkElement? HeaderExtra
    {
        get => _headerExtra.Content as FrameworkElement;
        set => _headerExtra.Content = value;
    }

    public void SetPinned(bool pinned, bool raise)
    {
        IsPinned = pinned;
        _pin.IsChecked = pinned;
        ToolTipService.SetToolTip(_pin, pinned ? "固定在边缘（点击改为悬浮，不占用终端空间）" : "悬浮在终端上方（点击固定到边缘）");
        if (raise) PinChanged?.Invoke();
    }
}
