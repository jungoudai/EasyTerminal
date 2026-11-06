using System.Runtime.InteropServices;
using EasyTerminal.Controls;
using EasyTerminal.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace EasyTerminal.Sessions;

/// <summary>
/// A small window, on top of others unless unpinned and optionally see-through, that keeps a running terminal in view while the user works elsewhere.
/// It only watches: the session still lives in its tab, which is where it is typed into and resized.
/// </summary>
public sealed class MiniWindow : Window
{
    private readonly TerminalView _view = new(mirror: true);
    private readonly TextBlock _title = Ui.Body("");
    private readonly Grid _root = new();
    private readonly Border _hint;
    private readonly Button _pin;
    private bool _topmost;

    /// <summary>Below this the window is too faint to read or find again.</summary>
    private const int MinOpacity = 30;
    private const string GlyphPinned = "";
    private const string GlyphUnpinned = "";
    private const string GlyphOpacity = "";

    public SessionTab Tab { get; }

#if DEBUG
    internal TerminalView ViewForTest => _view;

    /// <summary>The alpha Windows actually holds for the window, or -1 when it is not layered.</summary>
    internal int LayeredAlphaForTest =>
        GetLayeredWindowAttributes(WindowNative.GetWindowHandle(this), out _, out byte alpha, out uint flags) && (flags & LWA_ALPHA) != 0 ? alpha : -1;

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint key, out byte alpha, out uint flags);
#endif

    /// <summary>The user wants the session back in the main window.</summary>
    public event Action? ReturnRequested;

    public MiniWindow(SessionTab tab, TerminalAppearance appearance)
    {
        Tab = tab;
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // The caption buttons sit over the right end of the title row; only the close button is enabled.
        var titleRow = new Grid { ColumnSpacing = 4, Padding = new Thickness(10, 0, 0, 0) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        var icon = Ui.Icon(tab.Glyph, 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(icon);

        var drag = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _title.FontSize = 12;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(4, 0, 0, 0);
        drag.Children.Add(_title);
        Grid.SetColumn(drag, 1);
        titleRow.Children.Add(drag);

        var settings = AppServices.Current.Store.Data.Settings;
        _pin = TitleButton("", "", () => SetTopmost(!_topmost, save: true));
        var opacity = TitleButton(GlyphOpacity, "透明度", () => { });
        opacity.Flyout = BuildOpacityFlyout(settings.MiniWindowOpacity);
        var back = TitleButton(Ui.GlyphBackToWindow, "回到主窗口（双击画面也可以）", () => ReturnRequested?.Invoke());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_pin);
        buttons.Children.Add(opacity);
        buttons.Children.Add(back);
        Grid.SetColumn(buttons, 2);
        titleRow.Children.Add(buttons);
        _root.Children.Add(titleRow);

        _view.SetAppearance(appearance with { BackgroundOpacity = 1f });
        _view.Attach(tab.Terminal!);
        var frame = new Border { Child = _view, CornerRadius = new CornerRadius(6, 6, 0, 0), Background = new SolidColorBrush(appearance.Scheme.Background) };
        Grid.SetRow(frame, 1);
        _root.Children.Add(frame);

        // The view ignores the pointer, so a hover hint over it tells the user this window only watches.
        var hintText = new TextBlock { Text = "只读小窗 · 双击回到主窗口", FontSize = 12 };
        _hint = new Border
        {
            Child = hintText,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(4),
            Background = Ui.Brush("SolidBackgroundFillColorBaseBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Grid.SetRow(_hint, 1);
        _root.Children.Add(_hint);
        frame.PointerEntered += (_, _) => _hint.Opacity = 0.9;
        frame.PointerExited += (_, _) => _hint.Opacity = 0;
        frame.DoubleTapped += (_, _) => ReturnRequested?.Invoke();

        Content = _root;
        SetTitleBar(drag);
        ApplyTheme(AppServices.Current.Store.Data.Settings.Theme);
        UpdateTitle();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "EasyTerminal.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        PlaceInCorner();
        SetTopmost(settings.MiniWindowTopmost, save: false);
        SetOpacity(settings.MiniWindowOpacity);

        Closed += (_, _) => _view.Dispose();
    }

    public bool IsTopmost => _topmost;

    /// <summary>Whole-window opacity in percent, as last applied.</summary>
    public int OpacityPercent { get; private set; } = 100;

    private static Button TitleButton(string glyph, string tooltip, Action onClick)
    {
        var button = Ui.IconButton(glyph, tooltip, onClick);
        button.Padding = new Thickness(8, 4, 8, 4);
        return button;
    }

    /// <summary>Keeps the window above others, or lets it fall behind like any other window.</summary>
    public void SetTopmost(bool topmost, bool save)
    {
        _topmost = topmost;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = topmost;
        // Filled pin while it stays on top; the outline means it can go behind other windows.
        ((FontIcon)_pin.Content).Glyph = topmost ? GlyphPinned : GlyphUnpinned;
        ToolTipService.SetToolTip(_pin, topmost ? "已置顶（点击取消）" : "置于顶层");
        if (!save) return;
        AppServices.Current.Store.Data.Settings.MiniWindowTopmost = topmost;
        AppServices.Current.Store.Save();
    }

    private Flyout BuildOpacityFlyout(int initial)
    {
        var value = Ui.Secondary("");
        var slider = new Slider { Minimum = MinOpacity, Maximum = 100, StepFrequency = 5, Value = Math.Clamp(initial, MinOpacity, 100), Width = 200 };
        void Show() => value.Text = $"{(int)slider.Value}%";
        slider.ValueChanged += (_, e) =>
        {
            SetOpacity((int)e.NewValue);
            Show();
        };
        Show();
        var header = Ui.Columns("*", "auto");
        var label = Ui.Body("窗口透明度");
        header.Children.Add(label);
        Grid.SetColumn(value, 1);
        header.Children.Add(value);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(header);
        panel.Children.Add(slider);
        var flyout = new Flyout { Content = panel };
        // Saved once the flyout closes, not on every step of a drag.
        flyout.Closed += (_, _) =>
        {
            AppServices.Current.Store.Data.Settings.MiniWindowOpacity = OpacityPercent;
            AppServices.Current.Store.Save();
        };
        return flyout;
    }

    /// <summary>Fades the whole window, title bar included, so what is behind it shows through.</summary>
    public void SetOpacity(int percent)
    {
        percent = Math.Clamp(percent, MinOpacity, 100);
        OpacityPercent = percent;
        nint hwnd = WindowNative.GetWindowHandle(this);
        nint style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((style & WS_EX_LAYERED) == 0) SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(percent * 255 / 100.0), LWA_ALPHA);
    }

    public void SetAppearance(TerminalAppearance appearance)
    {
        _view.SetAppearance(appearance with { BackgroundOpacity = 1f });
        if (_view.Parent is Border frame) frame.Background = new SolidColorBrush(appearance.Scheme.Background);
        ApplyTheme(AppServices.Current.Store.Data.Settings.Theme);
    }

    public void UpdateTitle()
    {
        _title.Text = Tab.Title;
        Title = $"{Tab.Title} - 小窗";
    }

    private void ApplyTheme(string theme) => _root.RequestedTheme = theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Starts in the bottom-right corner of the screen the main window is on, out of the way.</summary>
    private void PlaceInCorner()
    {
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        int width = (int)(480 * scale), height = (int)(300 * scale), margin = (int)(16 * scale);
        var mainId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(MainWindow.WindowHandle);
        var area = DisplayArea.GetFromWindowId(mainId, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + area.Width - width - margin, area.Y + area.Height - height - margin, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
}
