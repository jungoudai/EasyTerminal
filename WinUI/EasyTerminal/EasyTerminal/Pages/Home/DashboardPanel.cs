using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EasyTerminal.Pages.Home;

/// <summary>
/// Lays out home panels on a twelve-column grid, filling rows left to right. Panels in a row share the
/// tallest one's height, and the last panel in a row stretches over columns the next one could not use,
/// so rows never end in a gap. Narrower windows switch to halves, then to one panel per row.
/// </summary>
public sealed class DashboardPanel : Panel
{
    public const int Columns = 12;
    public double Spacing { get; set; } = 12;

    private static int SpanOf(UIElement child) => child is WidgetFrame frame ? Math.Clamp(frame.Span, 1, Columns) : Columns;

    public static int EffectiveSpan(int span, double width) =>
        double.IsInfinity(width) || width < 720 ? Columns : width < 1080 ? (span < Columns ? 6 : Columns) : span;

    /// <summary>Rows of (child index, columns), each row's spans adding up to twelve.</summary>
    public static List<List<(int Index, int Span)>> Rows(IReadOnlyList<int> spans)
    {
        var rows = new List<List<(int, int)>>();
        var current = new List<(int Index, int Span)>();
        int used = 0;
        void Close()
        {
            if (current.Count == 0) return;
            var last = current[^1];
            current[^1] = (last.Index, last.Span + Columns - used);
            rows.Add(current);
            current = new List<(int, int)>();
            used = 0;
        }
        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i] <= 0) continue;
            int span = Math.Min(spans[i], Columns);
            if (used + span > Columns) Close();
            current.Add((i, span));
            used += span;
        }
        Close();
        return rows;
    }

    private List<List<(int Index, int Span)>> Rows(double width) =>
        Rows(Children.Select(c => c.Visibility == Visibility.Collapsed ? 0 : EffectiveSpan(SpanOf(c), width)).ToList());

    private double WidthOf(int span, double width) => span * (width - Spacing * (Columns - 1)) / Columns + (span - 1) * Spacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        var rows = Rows(width);
        double height = Spacing * Math.Max(0, rows.Count - 1);
        foreach (var row in rows)
        {
            double tallest = 0;
            foreach (var (index, span) in row)
            {
                Children[index].Measure(new Size(WidthOf(span, width), double.PositiveInfinity));
                tallest = Math.Max(tallest, Children[index].DesiredSize.Height);
            }
            height += tallest;
        }
        foreach (var child in Children)
            if (child.Visibility == Visibility.Collapsed) child.Measure(new Size(0, 0));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = finalSize.Width;
        double y = 0;
        foreach (var row in Rows(width))
        {
            double tallest = row.Max(cell => Children[cell.Index].DesiredSize.Height);
            double x = 0;
            foreach (var (index, span) in row)
            {
                double w = WidthOf(span, width);
                Children[index].Arrange(new Rect(x, y, w, tallest));
                x += w + Spacing;
            }
            y += tallest + Spacing;
        }
        return finalSize;
    }
}
