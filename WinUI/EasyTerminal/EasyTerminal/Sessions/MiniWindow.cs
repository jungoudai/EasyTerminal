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
/// A small always-on-top window that keeps a running terminal in view while the user works elsewhere.
/// It only watches: the session still lives in its tab, which is where it is typed into and resized.
/// </summary>
public sealed class MiniWindow : Window
{
    private readonly TerminalView _view = new(mirror: true);
    private readonly TextBlock _title = Ui.Body("");
    private readonly Grid _root = new();
    private readonly Border _hint;

    public SessionTab Tab { get; }

#if DEBUG
    internal TerminalView ViewForTest => _view;
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

        var back = Ui.IconButton(Ui.GlyphBackToWindow, "回到主窗口（双击画面也可以）", () => ReturnRequested?.Invoke());
        back.Padding = new Thickness(8, 4, 8, 4);
        back.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(back, 2);
        titleRow.Children.Add(back);
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
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "EasyTerminal.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        PlaceInCorner();

        Closed += (_, _) => _view.Dispose();
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
}
