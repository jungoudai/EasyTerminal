using System.Numerics;
using EasyTerminal.Core.Terminal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace EasyTerminal.Controls;

public sealed record TerminalAppearance(string FontFamily, float FontSize, float LineHeight, ColorScheme Scheme, bool CursorBlink, bool BoldIsBright);

/// <summary>
/// The terminal surface: a swap chain drawn on a dedicated thread at most once per vblank, a hidden
/// TextBox as the IME and text input sink, and pointer handling for selection and mouse reporting.
/// </summary>
public sealed partial class TerminalView : Grid, IDisposable
{
    private const float Padding = 6f;
    private const int BlinkMilliseconds = 530;

    private readonly CanvasSwapChainPanel _panel = new();
    private readonly ScrollBar _scrollBar = new() { Orientation = Orientation.Vertical, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBox _input = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly AutoResetEvent _wake = new(false);
    private readonly DispatcherQueueTimer _resizeTimer;
    private readonly object _renderLock = new();
    private readonly Canvas _inputLayer = new() { IsHitTestVisible = false };

    private TerminalSession? _session;
    private TerminalAppearance _appearance = new("Cascadia Mono", 13, 1.15f, ColorScheme.All[0], true, true);
    private Thread? _renderThread;
    private volatile bool _stopRender;
    private volatile bool _visible = true;
    private volatile bool _focused;
    private volatile bool _dirty = true;
    private bool _composing;
    private bool _syncingScrollBar;

    private CanvasDevice? _device;
    private CanvasSwapChain? _swapChain;
    private GlyphAtlas? _atlas;
    private float _dpi = 96;
    private float _cellWidth = 8, _cellHeight = 18, _baselineOffset;
    private float _viewWidth = 1, _viewHeight = 1;
    private bool _metricsDirty = true;
    private bool _sizeDirty = true;
    private long _lastCursorActivity;
    private long _lastDrawnVersion = -1;

    // Selection is in absolute rows so it stays on the same text while output scrolls.
    private bool _hasSelection;
    private long _selStartRow, _selEndRow;
    private int _selStartCol, _selEndCol;
    private bool _selecting;
    private bool _mousePressedForTerminal;
    private MouseButton _pressedButton;
    private (int Col, int Row) _lastMouseCell;
    private long _lastClickTicks;
    private int _clickCount;
    private Point _lastClickPoint;

    public event EventHandler? FontSizeChangeRequested;
    public event EventHandler? CommandPaletteRequested;
    public event EventHandler? FindRequested;
    /// <summary>Raised with what the user typed or pasted, so it can be mirrored to other sessions.</summary>
    public event Action<byte[]>? InputSent;
    /// <summary>Lets the session add its own entries to the right-click menu.</summary>
    public Action<MenuFlyout>? ExtendContextMenu { get; set; }
    /// <summary>Window shortcuts (Ctrl+Shift+I for the AI panel, Ctrl+Shift+S for snippets) that a focused terminal would otherwise swallow.</summary>
    public static Action<VirtualKey>? AppShortcut { get; set; }

    private static volatile bool s_windowVisible = true;
    private static event Action? WindowShown;

    /// <summary>False while the window is minimized; terminals skip drawing until it is shown again.</summary>
    public static bool WindowVisible
    {
        get => s_windowVisible;
        set
        {
            if (s_windowVisible == value) return;
            s_windowVisible = value;
            if (value) WindowShown?.Invoke();
        }
    }

    public TerminalView()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _resizeTimer = _dispatcher.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(40);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => ApplyLayoutSize();

        ConfigureInput();
        _inputLayer.Children.Add(_input);
        Children.Add(_panel);
        Children.Add(_inputLayer);
        Children.Add(_scrollBar);

        _panel.PointerPressed += OnPointerPressed;
        _panel.PointerMoved += OnPointerMoved;
        _panel.PointerReleased += OnPointerReleased;
        _panel.PointerWheelChanged += OnPointerWheelChanged;
        _panel.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
        _panel.PointerExited += (_, _) => ProtectedCursor = null;
        _scrollBar.ValueChanged += OnScrollBarChanged;
        _scrollBar.Visibility = Visibility.Collapsed;
        IsTabStop = false;

        SizeChanged += (_, _) => _resizeTimer.Start();
        WindowShown += Invalidate;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Attach(TerminalSession session)
    {
        if (_session != null) _session.Updated -= OnSessionUpdated;
        _session = session;
        session.Updated += OnSessionUpdated;
        lock (_renderLock) _dirty = true;
        _wake.Set();
        _resizeTimer.Start();
    }

    public (int Cols, int Rows) MeasureGrid()
    {
        EnsureMetrics();
        return ComputeGrid();
    }

    private (int Cols, int Rows) ComputeGrid()
    {
        float width = Math.Max((float)ActualWidth - Padding * 2, _cellWidth);
        float height = Math.Max((float)ActualHeight - Padding * 2, _cellHeight);
        return (Math.Max((int)(width / _cellWidth), 2), Math.Max((int)(height / _cellHeight), 1));
    }

    public void SetAppearance(TerminalAppearance appearance)
    {
        lock (_renderLock)
        {
            _appearance = appearance;
            _metricsDirty = true;
            _dirty = true;
        }
        if (_input.FontSize != appearance.FontSize)
        {
            _input.FontSize = appearance.FontSize;
            _input.FontFamily = new FontFamily(appearance.FontFamily);
        }
        Background = new SolidColorBrush(appearance.Scheme.Background);
        _wake.Set();
        if (IsLoaded) _resizeTimer.Start();
    }

    public void SetActive(bool active)
    {
        _visible = active;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            _dirty = true;
            _wake.Set();
        }
    }

    public void FocusTerminal() => _input.Focus(FocusState.Programmatic);

    private void OnSessionUpdated()
    {
        _dirty = true;
        _wake.Set();
    }

    // ---- Lifecycle ---------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        XamlRoot.Changed += OnXamlRootChanged;
        _dpi = 96f * (float)XamlRoot.RasterizationScale;
        lock (_renderLock)
        {
            EnsureDevice();
            _metricsDirty = true;
            EnsureMetrics();
        }
        ApplyLayoutSize();

        if (_renderThread == null)
        {
            _stopRender = false;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Terminal render" };
            _renderThread.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot != null) XamlRoot.Changed -= OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        float dpi = 96f * (float)sender.RasterizationScale;
        if (dpi == _dpi) return;
        lock (_renderLock)
        {
            _dpi = dpi;
            _metricsDirty = true;
            _sizeDirty = true;
            _dirty = true;
        }
        _resizeTimer.Start();
        _wake.Set();
    }

    public void Dispose()
    {
        WindowShown -= Invalidate;
        _stopRender = true;
        _wake.Set();
        _renderThread?.Join(500);
        if (_session != null) _session.Updated -= OnSessionUpdated;
        lock (_renderLock)
        {
            _atlas?.Dispose();
            _atlas = null;
            _swapChain?.Dispose();
            _swapChain = null;
        }
    }

    private void EnsureDevice() => _device ??= CanvasDevice.GetSharedDevice();

    private void EnsureMetrics()
    {
        lock (_renderLock)
        {
            if (!_metricsDirty) return;
            EnsureDevice();
            using var format = new CanvasTextFormat
            {
                FontFamily = _appearance.FontFamily,
                FontSize = _appearance.FontSize,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            using var layout = new CanvasTextLayout(_device!, "M", format, 0, 0);
            float scale = _dpi / 96f;
            var bounds = layout.LayoutBounds;
            // Snap cells to whole pixels so sprites never need resampling and rows never show seams.
            float widthPixels = MathF.Max(MathF.Round((float)bounds.Width * scale), 1);
            float naturalHeight = (float)bounds.Height;
            float heightPixels = MathF.Max(MathF.Ceiling(naturalHeight * _appearance.LineHeight * scale), 1);
            _cellWidth = widthPixels / scale;
            _cellHeight = heightPixels / scale;
            _baselineOffset = MathF.Round((_cellHeight - naturalHeight) / 2 * scale) / scale;

            _atlas?.Dispose();
            _atlas = new GlyphAtlas(_device!, _appearance.FontFamily, _appearance.FontSize, _dpi, _cellWidth, _cellHeight, _baselineOffset);
            _metricsDirty = false;
        }
    }

    private void ApplyLayoutSize()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        lock (_renderLock)
        {
            EnsureMetrics();
            _viewWidth = (float)ActualWidth;
            _viewHeight = (float)ActualHeight;
            _sizeDirty = true;
            _dirty = true;
        }
        var (cols, rows) = ComputeGrid();
        _session?.Resize(cols, rows);
        _wake.Set();
    }

    // ---- Rendering ---------------------------------------------------------------------------

    private void RenderLoop()
    {
        while (!_stopRender)
        {
            // Wake on output, or for the next cursor blink.
            _wake.WaitOne(BlinkMilliseconds);
            if (_stopRender) break;
            if (!_visible || !s_windowVisible) continue;

            // Idle, unfocused terminals draw nothing at all.
            bool blinkTick = _appearance.CursorBlink && _focused;
            if (!_dirty && !blinkTick) continue;

            try
            {
                lock (_renderLock) DrawFrame();
            }
            catch (Exception ex) when (_device != null && _device.IsDeviceLost(ex.HResult))
            {
                HandleDeviceLost();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void HandleDeviceLost()
    {
        lock (_renderLock)
        {
            _device?.RaiseDeviceLost();
            _device = null;
            _atlas?.Dispose();
            _atlas = null;
            _swapChain?.Dispose();
            _swapChain = null;
            _metricsDirty = true;
            _sizeDirty = true;
            _dirty = true;
        }
    }

    private Cell[] _frame = Array.Empty<Cell>();

    private void DrawFrame()
    {
        var session = _session;
        if (session == null) return;
        EnsureMetricsOnRenderThread();

        float scale = _dpi / 96f;
        if (_swapChain == null)
        {
            _swapChain = new CanvasSwapChain(_device!, Math.Max(_viewWidth, 1), Math.Max(_viewHeight, 1), _dpi);
            var chain = _swapChain;
            _dispatcher.TryEnqueue(() => _panel.SwapChain = chain);
            _sizeDirty = false;
        }
        else if (_sizeDirty)
        {
            _swapChain.ResizeBuffers(Math.Max(_viewWidth, 1), Math.Max(_viewHeight, 1), _dpi);
            _sizeDirty = false;
        }

        var buffer = session.Buffer;
        var scheme = _appearance.Scheme;
        int cols, rows, cursorCol, cursorRow, scrollOffset, scrollbackLines;
        bool cursorVisible, reverse, blink, wantBlink;
        CursorShape shape;
        long selStartRow = 0, selEndRow = 0, topRow;
        int selStartCol = 0, selEndCol = 0;
        bool hasSelection;

        lock (buffer.SyncRoot)
        {
            if (buffer.SynchronizedOutput && !_sizeDirty && _lastDrawnVersion >= 0) return;
            cols = buffer.Cols;
            rows = buffer.Rows;
            if (_frame.Length != cols * rows) _frame = new Cell[cols * rows];
            for (int r = 0; r < rows; r++)
            {
                var line = buffer.ViewportLine(r).Cells;
                Array.Copy(line, 0, _frame, r * cols, Math.Min(cols, line.Length));
            }
            cursorCol = buffer.CursorCol;
            cursorRow = buffer.CursorRow;
            cursorVisible = buffer.CursorVisible && buffer.ScrollOffset == 0;
            shape = buffer.CursorShape;
            reverse = buffer.ReverseVideo;
            blink = buffer.CursorBlink;
            scrollOffset = buffer.ScrollOffset;
            scrollbackLines = buffer.ScrollbackLines;
            topRow = buffer.AbsoluteViewportRow(0);
            _lastDrawnVersion = buffer.Version;
            hasSelection = _hasSelection;
            selStartRow = _selStartRow;
            selEndRow = _selEndRow;
            selStartCol = _selStartCol;
            selEndCol = _selEndCol;
            ResolveCustomPalette(buffer);
        }

        wantBlink = blink && _appearance.CursorBlink && _focused;
        bool cursorPhaseOn = !wantBlink || (Environment.TickCount64 - _lastCursorActivity) % (BlinkMilliseconds * 2) < BlinkMilliseconds;
        _dirty = false;

        if (hasSelection && (selStartRow > selEndRow || (selStartRow == selEndRow && selStartCol > selEndCol)))
        {
            (selStartRow, selEndRow) = (selEndRow, selStartRow);
            (selStartCol, selEndCol) = (selEndCol, selStartCol);
        }

        var atlas = _atlas!;
        Color defaultBackground = reverse ? scheme.Foreground : scheme.Background;

        // Queue every glyph first so all rasterization happens outside the swap chain's session.
        for (int i = 0; i < _frame.Length; i++)
        {
            ref readonly var cell = ref _frame[i];
            if (cell.Codepoint is 0 or 32 || (cell.Flags & CellFlags.WideTail) != 0) continue;
            // Most cells are already in the atlas; skip the width lookup for those.
            if (atlas.Contains(cell.Codepoint, StyleOf(cell.Flags))) continue;
            atlas.Request(cell.Codepoint, StyleOf(cell.Flags), CharWidth.Of(FirstCodepoint(cell.Codepoint)) == 2);
        }
        atlas.Flush(_device!);

        float cw = _cellWidth, ch = _cellHeight;
        void Paint(CanvasDrawingSession ds)
        {
            ds.Antialiasing = CanvasAntialiasing.Aliased;

            // Backgrounds, coalesced into runs of the same color.
            for (int r = 0; r < rows; r++)
            {
                float y = Padding + r * ch;
                int runStart = 0;
                Color runColor = default;
                bool runActive = false;
                for (int c = 0; c <= cols; c++)
                {
                    Color color = default;
                    bool painted = false;
                    if (c < cols)
                    {
                        ref readonly var cell = ref _frame[r * cols + c];
                        (color, painted) = BackgroundOf(cell, scheme, reverse);
                        long abs = topRow + r;
                        if (hasSelection && InSelection(abs, c, selStartRow, selStartCol, selEndRow, selEndCol))
                        {
                            color = scheme.Selection;
                            painted = true;
                        }
                    }
                    if (runActive && (!painted || color != runColor))
                    {
                        ds.FillRectangle(Padding + runStart * cw, y, (c - runStart) * cw, ch, runColor);
                        runActive = false;
                    }
                    if (painted && !runActive)
                    {
                        runActive = true;
                        runStart = c;
                        runColor = color;
                    }
                }
            }

            bool drawCursor = cursorVisible && cursorPhaseOn && scrollOffset == 0 && cursorRow < rows && cursorCol < cols;
            bool blockCursor = drawCursor && shape == CursorShape.Block && _focused;
            float cursorX = Padding + cursorCol * cw;
            float cursorY = Padding + cursorRow * ch;
            int cursorCells = 1;
            if (drawCursor && cursorCol + 1 < cols && (_frame[cursorRow * cols + cursorCol + 1].Flags & CellFlags.WideTail) != 0) cursorCells = 2;
            if (blockCursor) ds.FillRectangle(cursorX, cursorY, cw * cursorCells, ch, scheme.Cursor);

            using (var sprites = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.NearestNeighbor, CanvasSpriteOptions.ClampToSourceRect))
            {
                for (int r = 0; r < rows; r++)
                {
                    float y = Padding + r * ch;
                    for (int c = 0; c < cols; c++)
                    {
                        ref readonly var cell = ref _frame[r * cols + c];
                        if (cell.Codepoint is 0 or 32 || (cell.Flags & (CellFlags.WideTail | CellFlags.Invisible)) != 0) continue;
                        Color fg = blockCursor && r == cursorRow && c == cursorCol ? BlockCursorText(cell, scheme) : ForegroundOf(cell, scheme, reverse);
                        var slot = atlas.Slot(cell.Codepoint, StyleOf(cell.Flags));
                        var tint = new Vector4(fg.R / 255f, fg.G / 255f, fg.B / 255f, 1f);
                        if (IsColorGlyph(cell.Codepoint)) tint = Vector4.One;
                        sprites.DrawFromSpriteSheet(atlas.Texture, new Rect(Padding + c * cw, y, slot.Width, slot.Height), slot, tint);
                    }
                }
            }

            // Decorations go on top of the glyphs.
            float lineThickness = MathF.Max(1f / scale, MathF.Round(_appearance.FontSize / 13f) / scale);
            for (int r = 0; r < rows; r++)
            {
                float y = Padding + r * ch;
                for (int c = 0; c < cols; c++)
                {
                    ref readonly var cell = ref _frame[r * cols + c];
                    if ((cell.Flags & (CellFlags.Underline | CellFlags.Strike)) == 0) continue;
                    Color fg = ForegroundOf(cell, scheme, reverse);
                    if ((cell.Flags & CellFlags.Underline) != 0) ds.FillRectangle(Padding + c * cw, y + ch - lineThickness * 2, cw, lineThickness, fg);
                    if ((cell.Flags & CellFlags.Strike) != 0) ds.FillRectangle(Padding + c * cw, y + ch / 2, cw, lineThickness, fg);
                }
            }

            if (drawCursor && !blockCursor)
            {
                if (!_focused) ds.DrawRectangle(cursorX + 0.5f, cursorY + 0.5f, cw * cursorCells - 1, ch - 1, scheme.Cursor, 1f);
                else if (shape == CursorShape.Bar) ds.FillRectangle(cursorX, cursorY, MathF.Max(2f / scale, 1.5f), ch, scheme.Cursor);
                else ds.FillRectangle(cursorX, cursorY + ch - 2f, cw * cursorCells, 2f, scheme.Cursor);
            }
        }

        using (var frame = _swapChain.CreateDrawingSession(defaultBackground)) Paint(frame);

#if DEBUG
        if (Interlocked.Exchange(ref _frameCapture, null) is { } capture)
        {
            try
            {
                using var target = new CanvasRenderTarget(_device!, _viewWidth, _viewHeight, _dpi);
                using (var copy = target.CreateDrawingSession())
                {
                    copy.Clear(defaultBackground);
                    Paint(copy);
                }
                target.SaveAsync(capture.Path).AsTask().GetAwaiter().GetResult();
                capture.Done.TrySetResult();
            }
            catch (Exception ex)
            {
                capture.Done.TrySetException(ex);
            }
        }
#endif
        _swapChain.Present(1);
        PostCursorAndScroll(cursorRow, cursorCol, scrollbackLines, scrollOffset, rows);
    }

#if DEBUG
    private sealed record FrameCapture(string Path, TaskCompletionSource Done);
    private FrameCapture? _frameCapture;

    /// <summary>Saves the next drawn frame to a PNG; used by the debug self-test.</summary>
    public Task SaveFrameAsync(string path)
    {
        var capture = new FrameCapture(path, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _frameCapture = capture;
        Invalidate();
        return capture.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
#endif

    private void EnsureMetricsOnRenderThread()
    {
        EnsureDevice();
        if (_metricsDirty) EnsureMetrics();
        if (_atlas == null)
        {
            _metricsDirty = true;
            EnsureMetrics();
        }
    }

    private int[]? _customPalette;

    private void ResolveCustomPalette(TerminalBuffer buffer)
    {
        _customPalette ??= new int[256];
        for (int i = 0; i < 256; i++) _customPalette[i] = buffer.ResolvePalette(i);
    }

    private static int StyleOf(CellFlags flags) => ((flags & CellFlags.Bold) != 0 ? 1 : 0) | ((flags & CellFlags.Italic) != 0 ? 2 : 0);

    private static int FirstCodepoint(int codepoint) =>
        ClusterTable.IsCluster(codepoint) ? char.ConvertToUtf32(ClusterTable.Get(codepoint), 0) : codepoint;

    private static bool IsColorGlyph(int codepoint)
    {
        int first = FirstCodepoint(codepoint);
        return first is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF && CharWidth.Of(first) == 2;
    }

    private Color ResolveWithPalette(int color, bool isForeground, ColorScheme scheme)
    {
        if (color == TermColor.Default) return isForeground ? scheme.Foreground : scheme.Background;
        if (TermColor.IsPalette(color))
        {
            int index = color & 0xFF;
            int custom = _customPalette![index];
            if (custom == 0) return scheme.Palette[index];
            color = custom;
        }
        return Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    }

    private (Color Color, bool Painted) BackgroundOf(in Cell cell, ColorScheme scheme, bool reverse)
    {
        bool inverse = ((cell.Flags & CellFlags.Inverse) != 0) ^ reverse;
        if (!inverse)
        {
            if (cell.Background == 0) return (default, false);
            return (ResolveWithPalette(cell.Background, false, scheme), true);
        }
        // Screen-wide reverse with the default foreground matches the cleared background.
        if (reverse && (cell.Flags & CellFlags.Inverse) == 0 && cell.Foreground == 0) return (default, false);
        return (ResolveWithPalette(cell.Foreground, true, scheme), true);
    }

    private Color ForegroundOf(in Cell cell, ColorScheme scheme, bool reverse)
    {
        bool inverse = ((cell.Flags & CellFlags.Inverse) != 0) ^ reverse;
        int source = inverse ? cell.Background : cell.Foreground;
        if (!inverse && _appearance.BoldIsBright && (cell.Flags & CellFlags.Bold) != 0 && TermColor.IsPalette(source) && (source & 0xFF) < 8)
            source = TermColor.Palette((source & 0xFF) + 8);
        Color color = inverse
            ? (source == 0 ? scheme.Background : ResolveWithPalette(source, false, scheme))
            : ResolveWithPalette(source, true, scheme);
        if ((cell.Flags & CellFlags.Faint) != 0)
        {
            var bg = scheme.Background;
            color = Color.FromArgb(255, (byte)((color.R + bg.R) / 2), (byte)((color.G + bg.G) / 2), (byte)((color.B + bg.B) / 2));
        }
        return color;
    }

    private static Color BlockCursorText(in Cell cell, ColorScheme scheme) => scheme.Background;

    private static bool InSelection(long row, int col, long startRow, int startCol, long endRow, int endCol)
    {
        if (row < startRow || row > endRow) return false;
        if (row == startRow && col < startCol) return false;
        if (row == endRow && col > endCol) return false;
        return true;
    }

    private int _postedScrollback = -1, _postedOffset = -1, _postedRows = -1, _postedCursorRow = -1, _postedCursorCol = -1;

    private void PostCursorAndScroll(int cursorRow, int cursorCol, int scrollback, int offset, int rows)
    {
        if (scrollback == _postedScrollback && offset == _postedOffset && rows == _postedRows && cursorRow == _postedCursorRow && cursorCol == _postedCursorCol) return;
        _postedScrollback = scrollback;
        _postedOffset = offset;
        _postedRows = rows;
        _postedCursorRow = cursorRow;
        _postedCursorCol = cursorCol;
        float x = Padding + cursorCol * _cellWidth;
        float y = Padding + cursorRow * _cellHeight;
        _dispatcher.TryEnqueue(() =>
        {
            Canvas.SetLeft(_input, x);
            Canvas.SetTop(_input, y);
            _syncingScrollBar = true;
            _scrollBar.Visibility = scrollback > 0 ? Visibility.Visible : Visibility.Collapsed;
            _scrollBar.Maximum = scrollback;
            _scrollBar.ViewportSize = rows;
            _scrollBar.LargeChange = rows;
            _scrollBar.SmallChange = 1;
            _scrollBar.Value = scrollback - offset;
            _syncingScrollBar = false;
        });
    }

    private void OnScrollBarChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScrollBar || _session == null) return;
        var buffer = _session.Buffer;
        lock (buffer.SyncRoot) buffer.ScrollViewport((int)(buffer.ScrollbackLines - e.NewValue) - buffer.ScrollOffset);
        Invalidate();
    }

    private void Invalidate()
    {
        _dirty = true;
        _wake.Set();
    }

    // ---- Text input and keyboard -------------------------------------------------------------

    private void ConfigureInput()
    {
        _input.Width = 4;
        _input.MinWidth = 0;
        _input.MinHeight = 0;
        _input.Padding = new Thickness(0);
        _input.BorderThickness = new Thickness(0);
        _input.Opacity = 0;
        _input.IsSpellCheckEnabled = false;
        _input.IsTextPredictionEnabled = false;
        _input.AcceptsReturn = false;
        _input.TextWrapping = TextWrapping.NoWrap;
        _input.Background = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBackground" })
            _input.Resources[key] = new SolidColorBrush(Color.FromArgb(255, 40, 44, 52));
        _input.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);

        _input.PreviewKeyDown += OnPreviewKeyDown;
        _input.TextChanged += OnInputTextChanged;
        _input.TextCompositionStarted += (_, _) =>
        {
            _composing = true;
            _input.Opacity = 1;
            _input.Width = double.NaN;
        };
        _input.TextCompositionEnded += (_, _) =>
        {
            _composing = false;
            _input.Opacity = 0;
            _input.Width = 4;
            _dispatcher.TryEnqueue(FlushInputText);
        };
        _input.GotFocus += (_, _) => SetFocused(true);
        _input.LostFocus += (_, _) => SetFocused(false);
        _input.Paste += (_, e) =>
        {
            // Pasting is handled by us so it goes through bracketed-paste processing.
            e.Handled = true;
            _ = PasteAsync();
        };
    }

    private void SetFocused(bool focused)
    {
        _focused = focused;
        _lastCursorActivity = Environment.TickCount64;
        var session = _session;
        if (session != null && session.Buffer.FocusReporting) session.Write(focused ? "\x1b[I" : "\x1b[O");
        Invalidate();
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_composing) FlushInputText();
    }

    private void FlushInputText()
    {
        if (_composing || _input.Text.Length == 0) return;
        string text = _input.Text;
        _input.Text = "";
        SendText(text);
    }

    private void SendText(string text)
    {
        var session = _session;
        if (session == null) return;
        _lastCursorActivity = Environment.TickCount64;
        session.Buffer.ScrollToBottom();
        ClearSelection();
        session.Write(text);
        InputSent?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
        Invalidate();
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_composing || e.Key == (VirtualKey)229) return;
        var session = _session;
        if (session == null) return;

        bool control = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        var mods = (shift ? KeyModifiers.Shift : 0) | (alt ? KeyModifiers.Alt : 0) | (control ? KeyModifiers.Control : 0);

        // Terminal shortcuts come first and are always Ctrl+Shift, since plain Ctrl belongs to the program.
        if (control && shift && !alt)
        {
            switch (e.Key)
            {
                case VirtualKey.C:
                    CopySelection();
                    e.Handled = true;
                    return;
                case VirtualKey.V:
                    _ = PasteAsync();
                    e.Handled = true;
                    return;
                case VirtualKey.A:
                    SelectAll();
                    e.Handled = true;
                    return;
                case VirtualKey.P:
                    CommandPaletteRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                case VirtualKey.F:
                    FindRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                case VirtualKey.I or VirtualKey.S when AppShortcut != null:
                    AppShortcut(e.Key);
                    e.Handled = true;
                    return;
            }
        }
        if (shift && !control && !alt)
        {
            if (e.Key == VirtualKey.PageUp)
            {
                session.Buffer.ScrollViewport(session.Buffer.Rows - 1);
                Invalidate();
                e.Handled = true;
                return;
            }
            if (e.Key == VirtualKey.PageDown)
            {
                session.Buffer.ScrollViewport(-(session.Buffer.Rows - 1));
                Invalidate();
                e.Handled = true;
                return;
            }
        }
        if (control && !alt && !shift && e.Key is (VirtualKey)187 or (VirtualKey)189)
        {
            FontSizeDelta = e.Key == (VirtualKey)187 ? 1 : -1;
            FontSizeChangeRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (MapSpecialKey(e.Key) is { } special)
        {
            SendText(InputEncoder.EncodeKey(special, mods, session.Buffer.AppCursorKeys));
            e.Handled = true;
            return;
        }

        // AltGr reports Ctrl+Alt and is how many layouts type symbols, so it goes to the text box.
        if (control && !alt && !shift)
        {
            char? c = VirtualKeyToChar(e.Key);
            if (c != null && InputEncoder.ControlByte(c.Value) is { } controlByte)
            {
                SendBytes(new[] { controlByte });
                e.Handled = true;
            }
            return;
        }
        if (control && !alt && shift && VirtualKeyToChar(e.Key) is { } shifted && InputEncoder.ControlByte(shifted) is { } shiftedControl)
        {
            SendBytes(new[] { shiftedControl });
            e.Handled = true;
            return;
        }

        if (alt && !control)
        {
            char? c = VirtualKeyToChar(e.Key);
            if (c != null)
            {
                char letter = shift ? char.ToUpperInvariant(c.Value) : char.ToLowerInvariant(c.Value);
                SendText("\x1b" + letter);
                e.Handled = true;
            }
        }
    }

    public int FontSizeDelta { get; private set; }

    private void SendBytes(byte[] bytes)
    {
        var session = _session;
        if (session == null) return;
        _lastCursorActivity = Environment.TickCount64;
        session.Buffer.ScrollToBottom();
        ClearSelection();
        session.Write(bytes);
        InputSent?.Invoke(bytes);
        Invalidate();
    }

    private static char? VirtualKeyToChar(VirtualKey key)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z) return (char)('a' + (key - VirtualKey.A));
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9) return (char)('0' + (key - VirtualKey.Number0));
        return (int)key switch
        {
            32 => ' ',
            219 => '[',
            220 => '\\',
            221 => ']',
            189 => '-',
            191 => '?',
            _ => null,
        };
    }

    private static TerminalKey? MapSpecialKey(VirtualKey key) => key switch
    {
        VirtualKey.Up => TerminalKey.Up,
        VirtualKey.Down => TerminalKey.Down,
        VirtualKey.Left => TerminalKey.Left,
        VirtualKey.Right => TerminalKey.Right,
        VirtualKey.Home => TerminalKey.Home,
        VirtualKey.End => TerminalKey.End,
        VirtualKey.PageUp => TerminalKey.PageUp,
        VirtualKey.PageDown => TerminalKey.PageDown,
        VirtualKey.Insert => TerminalKey.Insert,
        VirtualKey.Delete => TerminalKey.Delete,
        VirtualKey.F1 => TerminalKey.F1,
        VirtualKey.F2 => TerminalKey.F2,
        VirtualKey.F3 => TerminalKey.F3,
        VirtualKey.F4 => TerminalKey.F4,
        VirtualKey.F5 => TerminalKey.F5,
        VirtualKey.F6 => TerminalKey.F6,
        VirtualKey.F7 => TerminalKey.F7,
        VirtualKey.F8 => TerminalKey.F8,
        VirtualKey.F9 => TerminalKey.F9,
        VirtualKey.F10 => TerminalKey.F10,
        VirtualKey.F11 => TerminalKey.F11,
        VirtualKey.F12 => TerminalKey.F12,
        VirtualKey.Enter => TerminalKey.Enter,
        VirtualKey.Tab => TerminalKey.Tab,
        VirtualKey.Back => TerminalKey.Backspace,
        VirtualKey.Escape => TerminalKey.Escape,
        _ => null,
    };

    // ---- Clipboard ---------------------------------------------------------------------------

    public void CopySelection()
    {
        var session = _session;
        if (session == null || !_hasSelection) return;
        string text = session.Buffer.GetText(_selStartRow, _selStartCol, _selEndRow, _selEndCol);
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public async Task PasteAsync()
    {
        var session = _session;
        if (session == null) return;
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        string text = await content.GetTextAsync();
        if (text.Length > 0) SendText(InputEncoder.PrepareForPaste(text, session.Buffer.BracketedPaste));
    }

    public void SelectAll()
    {
        var session = _session;
        if (session == null) return;
        var buffer = session.Buffer;
        lock (buffer.SyncRoot)
        {
            _selStartRow = buffer.AbsoluteViewportRow(0) - (buffer.ScrollbackLines - buffer.ScrollOffset);
            _selStartCol = 0;
            _selEndRow = buffer.AbsoluteViewportRow(buffer.Rows - 1) + buffer.ScrollOffset;
            _selEndCol = buffer.Cols - 1;
        }
        _hasSelection = true;
        Invalidate();
    }

    public void ClearSelection()
    {
        if (!_hasSelection) return;
        _hasSelection = false;
        Invalidate();
    }

    public bool HasSelection => _hasSelection;

#if DEBUG
    internal void SendForTest(string text) => SendText(text);
#endif

    public string SelectedText
    {
        get
        {
            var session = _session;
            return session == null || !_hasSelection ? "" : session.Buffer.GetText(_selStartRow, _selStartCol, _selEndRow, _selEndCol);
        }
    }

    /// <summary>Selects the next match in the screen and scrollback, wrapping at the ends. Matches are found per row.</summary>
    public bool Find(string query, bool backward)
    {
        var session = _session;
        if (session == null || query.Length == 0) return false;
        var buffer = session.Buffer;
        lock (buffer.SyncRoot)
        {
            long first = buffer.AbsoluteViewportRow(0) - (buffer.ScrollbackLines - buffer.ScrollOffset);
            long last = buffer.AbsoluteViewportRow(buffer.Rows - 1) + buffer.ScrollOffset;
            long start = _hasSelection ? Math.Clamp(_selStartRow, first, last) : backward ? last : first;
            long count = last - first + 1;

            for (long step = 0; step <= count; step++)
            {
                long row = first + ((start - first + (backward ? -step : step)) % count + count) % count;
                string text = buffer.GetText(row, 0, row, buffer.Cols - 1);
                // On the row of the current match, continue past it; a full wrap may land on it again.
                int offset = -1;
                if (step == 0 && _hasSelection)
                {
                    int selected = buffer.GetText(row, 0, row, _selStartCol).Length - 1;
                    offset = backward
                        ? (selected > 0 ? text.LastIndexOf(query, selected - 1, StringComparison.OrdinalIgnoreCase) : -1)
                        : (selected + 1 < text.Length ? text.IndexOf(query, selected + 1, StringComparison.OrdinalIgnoreCase) : -1);
                }
                else if (text.Length > 0)
                {
                    offset = backward ? text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase) : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                }
                if (offset < 0) continue;

                _selStartRow = _selEndRow = row;
                _selStartCol = ColumnOfTextOffset(buffer, row, offset);
                _selEndCol = ColumnOfTextOffset(buffer, row, offset + query.Length - 1);
                _hasSelection = true;
                long top = buffer.AbsoluteViewportRow(0);
                if (row < top) buffer.ScrollViewport((int)(top - row));
                else if (row > top + buffer.Rows - 1) buffer.ScrollViewport(-(int)(row - (top + buffer.Rows - 1)));
                Invalidate();
                return true;
            }
        }
        return false;
    }

    /// <summary>Wide characters take two cells but one UTF-16 unit, so text offsets and columns differ.</summary>
    private static int ColumnOfTextOffset(Core.Terminal.TerminalBuffer buffer, long row, int offset)
    {
        for (int col = 0; col < buffer.Cols; col++)
            if (buffer.GetText(row, 0, row, col).Length > offset) return col;
        return buffer.Cols - 1;
    }

    // ---- Pointer -----------------------------------------------------------------------------

    private (int Col, int Row) CellAt(Point point)
    {
        int col = (int)((point.X - Padding) / _cellWidth);
        int row = (int)((point.Y - Padding) / _cellHeight);
        var buffer = _session!.Buffer;
        return (Math.Clamp(col, 0, buffer.Cols - 1), Math.Clamp(row, 0, buffer.Rows - 1));
    }

    private static KeyModifiers PointerModifiers(Windows.System.VirtualKeyModifiers keys) =>
        (keys.HasFlag(VirtualKeyModifiers.Shift) ? KeyModifiers.Shift : 0) |
        (keys.HasFlag(VirtualKeyModifiers.Menu) ? KeyModifiers.Alt : 0) |
        (keys.HasFlag(VirtualKeyModifiers.Control) ? KeyModifiers.Control : 0);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        FocusTerminal();
        var session = _session;
        if (session == null) return;
        var point = e.GetCurrentPoint(_panel);
        var (col, row) = CellAt(point.Position);
        var buffer = session.Buffer;
        var mods = PointerModifiers(e.KeyModifiers);
        _panel.CapturePointer(e.Pointer);

        // Shift bypasses mouse reporting, so text stays selectable inside full-screen programs.
        if (buffer.MouseTracking != MouseTrackingMode.None && !mods.HasFlag(KeyModifiers.Shift))
        {
            _pressedButton = point.Properties.IsRightButtonPressed ? MouseButton.Right : point.Properties.IsMiddleButtonPressed ? MouseButton.Middle : MouseButton.Left;
            _mousePressedForTerminal = true;
            _lastMouseCell = (col, row);
            session.Write(InputEncoder.EncodeMouse(buffer.MouseEncoding, _pressedButton, MouseAction.Press, col, row, mods));
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            ShowContextMenu(point.Position);
            e.Handled = true;
            return;
        }
        if (point.Properties.IsMiddleButtonPressed)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        long now = Environment.TickCount64;
        bool sameSpot = Math.Abs(point.Position.X - _lastClickPoint.X) < 4 && Math.Abs(point.Position.Y - _lastClickPoint.Y) < 4;
        _clickCount = now - _lastClickTicks < 400 && sameSpot ? _clickCount + 1 : 1;
        _lastClickTicks = now;
        _lastClickPoint = point.Position;

        long absolute = buffer.AbsoluteViewportRow(row);
        if (_clickCount == 2)
        {
            var (start, end) = buffer.WordAt(row, col);
            SetSelection(absolute, start, absolute, end);
            _selecting = false;
        }
        else if (_clickCount >= 3)
        {
            SetSelection(absolute, 0, absolute, buffer.Cols - 1);
            _selecting = false;
            _clickCount = 0;
        }
        else
        {
            _hasSelection = false;
            _selStartRow = _selEndRow = absolute;
            _selStartCol = _selEndCol = col;
            _selecting = true;
            Invalidate();
        }
        e.Handled = true;
    }

    private void SetSelection(long startRow, int startCol, long endRow, int endCol)
    {
        _selStartRow = startRow;
        _selStartCol = startCol;
        _selEndRow = endRow;
        _selEndCol = endCol;
        _hasSelection = true;
        Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var session = _session;
        if (session == null) return;
        var buffer = session.Buffer;
        var point = e.GetCurrentPoint(_panel);
        var (col, row) = CellAt(point.Position);

        if (buffer.MouseTracking is MouseTrackingMode.AnyEvent or MouseTrackingMode.ButtonEvent && !PointerModifiers(e.KeyModifiers).HasFlag(KeyModifiers.Shift))
        {
            bool reportable = buffer.MouseTracking == MouseTrackingMode.AnyEvent || _mousePressedForTerminal;
            if (reportable && (col, row) != _lastMouseCell)
            {
                _lastMouseCell = (col, row);
                var button = _mousePressedForTerminal ? _pressedButton : MouseButton.None;
                session.Write(InputEncoder.EncodeMouse(buffer.MouseEncoding, button, MouseAction.Move, col, row, PointerModifiers(e.KeyModifiers)));
            }
            return;
        }

        if (_selecting)
        {
            // Dragging past an edge scrolls, so a selection can extend beyond one screen.
            if (point.Position.Y < 0) buffer.ScrollViewport(1);
            else if (point.Position.Y > ActualHeight) buffer.ScrollViewport(-1);
            _selEndRow = buffer.AbsoluteViewportRow(row);
            _selEndCol = col;
            _hasSelection = _selEndRow != _selStartRow || _selEndCol != _selStartCol;
            Invalidate();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var session = _session;
        _panel.ReleasePointerCapture(e.Pointer);
        if (session == null) return;
        if (_mousePressedForTerminal)
        {
            _mousePressedForTerminal = false;
            var (col, row) = CellAt(e.GetCurrentPoint(_panel).Position);
            session.Write(InputEncoder.EncodeMouse(session.Buffer.MouseEncoding, _pressedButton, MouseAction.Release, col, row, PointerModifiers(e.KeyModifiers)));
            return;
        }
        _selecting = false;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var session = _session;
        if (session == null) return;
        var buffer = session.Buffer;
        var point = e.GetCurrentPoint(_panel);
        int delta = point.Properties.MouseWheelDelta;
        int lines = Math.Max(Math.Abs(delta) / 40, 1);
        var mods = PointerModifiers(e.KeyModifiers);
        e.Handled = true;

        if (buffer.MouseTracking != MouseTrackingMode.None && !mods.HasFlag(KeyModifiers.Shift))
        {
            var (col, row) = CellAt(point.Position);
            var button = delta > 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
            session.Write(InputEncoder.EncodeMouse(buffer.MouseEncoding, button, MouseAction.Press, col, row, mods));
            return;
        }
        if (buffer.AltScreenActive)
        {
            // Full-screen programs without mouse support still scroll through arrow keys.
            string key = InputEncoder.EncodeKey(delta > 0 ? TerminalKey.Up : TerminalKey.Down, KeyModifiers.None, buffer.AppCursorKeys);
            for (int i = 0; i < lines; i++) session.Write(key);
            return;
        }
        buffer.ScrollViewport(delta > 0 ? lines : -lines);
        Invalidate();
    }

    private void ShowContextMenu(Point position)
    {
        var flyout = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "复制", IsEnabled = _hasSelection, KeyboardAcceleratorTextOverride = "Ctrl+Shift+C", Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += (_, _) => CopySelection();
        var paste = new MenuFlyoutItem { Text = "粘贴", KeyboardAcceleratorTextOverride = "Ctrl+Shift+V", Icon = new SymbolIcon(Symbol.Paste) };
        paste.Click += (_, _) => _ = PasteAsync();
        var selectAll = new MenuFlyoutItem { Text = "全选", KeyboardAcceleratorTextOverride = "Ctrl+Shift+A", Icon = new SymbolIcon(Symbol.SelectAll) };
        selectAll.Click += (_, _) => SelectAll();
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(selectAll);
        ExtendContextMenu?.Invoke(flyout);
        flyout.ShowAt(_panel, position);
    }
}
