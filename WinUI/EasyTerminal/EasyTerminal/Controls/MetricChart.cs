using EasyTerminal.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace EasyTerminal.Controls;

public sealed record ChartSeries(string Name, IReadOnlyList<double?> Values);

/// <summary>
/// A small time-series line chart: thin lines on recessive gridlines, a legend when there are two series,
/// and a crosshair with a tooltip on hover. Gaps in the data stay gaps instead of being bridged.
/// </summary>
public sealed class MetricChart : Grid
{
    private const double AxisWidth = 58, TopPadding = 6, BottomPadding = 4;

    // Validated categorical slots: fixed per position, with their own step for each theme.
    private static readonly Color[] LightSeries = { Color.FromArgb(255, 0x2A, 0x78, 0xD6), Color.FromArgb(255, 0xEB, 0x68, 0x34) };
    private static readonly Color[] DarkSeries = { Color.FromArgb(255, 0x39, 0x87, 0xE5), Color.FromArgb(255, 0xD9, 0x59, 0x26) };

    private readonly Canvas _plot = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Canvas _hover = new() { IsHitTestVisible = false };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly TextBlock _title = new() { FontSize = 12, Foreground = Services.Ui.Brush("TextFillColorPrimaryBrush") };
    private readonly Border _tooltip;
    private readonly StackPanel _tooltipLines = new() { Spacing = 2 };
    private readonly int _capacity;
    private readonly int _secondsPerSample;
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private Func<double, string> _format = v => v.ToString("0.#");
    private double? _fixedMax;
    private double _max = 1;
    private double _plotWidth, _plotHeight;
    private string _legendNames = "";
    private readonly List<Line> _gridLines = new();
    private readonly List<TextBlock> _gridLabels = new();
    private readonly List<Microsoft.UI.Xaml.Shapes.Path> _seriesPaths = new();

    public MetricChart(string title, int capacity, int secondsPerSample)
    {
        _capacity = capacity;
        _secondsPerSample = secondsPerSample;
        _title.Text = title;
        RowSpacing = 4;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });

        var header = Services.Ui.Columns("*", "auto");
        header.Children.Add(_title.InColumn(0));
        header.Children.Add(_legend.InColumn(1));
        Children.Add(header);

        _tooltip = new Border
        {
            Child = _tooltipLines,
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Services.Ui.Brush("CardStrokeColorDefaultBrush"),
            Background = Services.Ui.Brush("SolidBackgroundFillColorBaseBrush"),
            Visibility = Visibility.Collapsed,
        };
        var host = new Grid();
        host.Children.Add(_plot);
        host.Children.Add(_hover);
        SetRow(host, 1);
        Children.Add(host);
        _hover.Children.Add(_tooltip);

        _plot.SizeChanged += (_, _) => Redraw();
        _plot.PointerMoved += (_, e) => ShowHover(e.GetCurrentPoint(_plot).Position.X);
        _plot.PointerExited += (_, _) => HideHover();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public void SetData(IReadOnlyList<ChartSeries> series, Func<double, string> format, double? fixedMax = null)
    {
        _series = series;
        _format = format;
        _fixedMax = fixedMax;
        UpdateLegend();
        Redraw();
    }

    private Color SeriesColor(int index)
    {
        var palette = ActualTheme == ElementTheme.Dark ? DarkSeries : LightSeries;
        return palette[index % palette.Length];
    }

    private void UpdateLegend()
    {
        // Rebuilding the same legend every sample would make it flicker.
        string names = string.Join("|", _series.Select(s => s.Name));
        if (names == _legendNames) return;
        _legendNames = names;
        _legend.Children.Clear();
        if (_series.Count < 2) return;
        for (int i = 0; i < _series.Count; i++)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            entry.Children.Add(new Rectangle { Width = 10, Height = 2, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(SeriesColor(i)), VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(new TextBlock { Text = _series[i].Name, FontSize = 12, Foreground = Services.Ui.Brush("TextFillColorSecondaryBrush") });
            _legend.Children.Add(entry);
        }
    }

    /// <summary>Rounds up to 1, 2 or 5 times a power of ten, so the axis label is a clean number.</summary>
    public static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double mantissa = value / power;
        return (mantissa <= 1 ? 1 : mantissa <= 2 ? 2 : mantissa <= 5 ? 5 : 10) * power;
    }

    private double X(int fromEnd) => AxisWidth + (_plotWidth - AxisWidth) * (1 - fromEnd / (double)(_capacity - 1));
    private double Y(double value) => TopPadding + (_plotHeight - TopPadding - BottomPadding) * (1 - Math.Min(value / _max, 1));

    /// <summary>Moves the existing gridlines, labels and lines into place; nothing is recreated, so updates don't flicker.</summary>
    private void Redraw()
    {
        _plotWidth = _plot.ActualWidth;
        _plotHeight = _plot.ActualHeight;
        if (_plotWidth <= AxisWidth || _plotHeight <= 0) return;

        if (_gridLines.Count == 0)
        {
            for (int i = 0; i < 3; i++)
            {
                var line = new Line { StrokeThickness = 1 };
                var label = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Right, Width = AxisWidth - 6 };
                _gridLines.Add(line);
                _gridLabels.Add(label);
                _plot.Children.Add(line);
                _plot.Children.Add(label);
            }
        }

        double top = _series.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        _max = _fixedMax ?? NiceCeiling(top);
        var gridBrush = Services.Ui.Brush("CardStrokeColorDefaultBrush");
        var labelBrush = Services.Ui.Brush("TextFillColorTertiaryBrush");
        double[] levels = { 0, 0.5, 1 };
        for (int i = 0; i < levels.Length; i++)
        {
            double y = Y(_max * levels[i]);
            var line = _gridLines[i];
            line.X1 = AxisWidth;
            line.X2 = _plotWidth;
            line.Y1 = line.Y2 = y;
            line.Stroke = gridBrush;
            _gridLabels[i].Text = _format(_max * levels[i]);
            _gridLabels[i].Foreground = labelBrush;
            Canvas.SetTop(_gridLabels[i], Math.Clamp(y - 8, 0, _plotHeight - 16));
        }

        while (_seriesPaths.Count < _series.Count)
        {
            var path = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _seriesPaths.Add(path);
            _plot.Children.Add(path);
        }
        for (int s = 0; s < _seriesPaths.Count; s++)
        {
            var path = _seriesPaths[s];
            if (s >= _series.Count)
            {
                path.Data = null;
                continue;
            }
            path.Stroke = new SolidColorBrush(SeriesColor(s));
            // One figure per unbroken run, so a missing reading stays a gap.
            var geometry = new PathGeometry();
            PathFigure? figure = null;
            var values = _series[s].Values;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } value)
                {
                    figure = null;
                    continue;
                }
                var point = new Point(X(values.Count - 1 - i), Y(value));
                if (figure == null)
                {
                    figure = new PathFigure { StartPoint = point, IsClosed = false, IsFilled = false };
                    figure.Segments.Add(new PolyLineSegment());
                    geometry.Figures.Add(figure);
                }
                else
                {
                    ((PolyLineSegment)figure.Segments[0]).Points.Add(point);
                }
            }
            path.Data = geometry;
        }
    }

    private void ShowHover(double x)
    {
        int count = _series.Count > 0 ? _series[0].Values.Count : 0;
        if (count == 0 || _plotWidth <= AxisWidth)
        {
            HideHover();
            return;
        }
        double step = (_plotWidth - AxisWidth) / (_capacity - 1);
        int fromEnd = Math.Clamp((int)Math.Round((_plotWidth - x) / step), 0, count - 1);
        int index = count - 1 - fromEnd;
        double pointX = X(fromEnd);

        for (int i = _hover.Children.Count - 1; i >= 0; i--)
            if (_hover.Children[i] != _tooltip) _hover.Children.RemoveAt(i);
        _hover.Children.Insert(0, new Line { X1 = pointX, X2 = pointX, Y1 = TopPadding, Y2 = _plotHeight - BottomPadding, Stroke = Services.Ui.Brush("TextFillColorTertiaryBrush"), StrokeThickness = 1 });

        _tooltipLines.Children.Clear();
        _tooltipLines.Children.Add(new TextBlock { Text = fromEnd == 0 ? "现在" : $"{fromEnd * _secondsPerSample} 秒前", FontSize = 11, Foreground = Services.Ui.Brush("TextFillColorSecondaryBrush") });
        for (int s = 0; s < _series.Count; s++)
        {
            double? value = index < _series[s].Values.Count ? _series[s].Values[index] : null;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Rectangle { Width = 10, Height = 2, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(SeriesColor(s)), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{_series[s].Name} {(value is { } v ? _format(v) : "无数据")}", FontSize = 12, Foreground = Services.Ui.Brush("TextFillColorPrimaryBrush") });
            _tooltipLines.Children.Add(row);
            if (value is { } point)
            {
                // A ring in the card's colour keeps the marker readable where lines cross.
                var marker = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(SeriesColor(s)), Stroke = Services.Ui.Brush("SolidBackgroundFillColorBaseBrush"), StrokeThickness = 2 };
                Canvas.SetLeft(marker, pointX - 4);
                Canvas.SetTop(marker, Y(point) - 4);
                _hover.Children.Insert(1, marker);
            }
        }

        _tooltip.Visibility = Visibility.Visible;
        _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = _tooltip.DesiredSize.Width;
        Canvas.SetLeft(_tooltip, pointX + 10 + width > _plotWidth ? pointX - 10 - width : pointX + 10);
        Canvas.SetTop(_tooltip, TopPadding);
    }

    private void HideHover()
    {
        for (int i = _hover.Children.Count - 1; i >= 0; i--)
            if (_hover.Children[i] != _tooltip) _hover.Children.RemoveAt(i);
        _tooltip.Visibility = Visibility.Collapsed;
    }
}

/// <summary>A thin vertical bar that reports horizontal drags, showing the resize cursor over it.</summary>
public sealed class ResizeHandle : Grid
{
    private bool _dragging;
    private double _last;

    public event Action<double>? Dragged;
    public event Action? DragEnded;

    public ResizeHandle()
    {
        Width = 6;
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        PointerPressed += (_, e) =>
        {
            _dragging = CapturePointer(e.Pointer);
            _last = e.GetCurrentPoint(null).Position.X;
        };
        PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            double x = e.GetCurrentPoint(null).Position.X;
            Dragged?.Invoke(x - _last);
            _last = x;
        };
        PointerReleased += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            DragEnded?.Invoke();
        };
    }
}
