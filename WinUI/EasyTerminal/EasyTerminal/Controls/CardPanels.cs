using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EasyTerminal.Controls;

/// <summary>Equal-width cards, as many per row as fit; the cards in a row share the tallest one's height.</summary>
public sealed class CardGridPanel : Panel
{
    public double MinCardWidth { get; set; } = 300;
    public double Spacing { get; set; } = 10;

    private int ColumnsFor(double width) =>
        double.IsInfinity(width) ? 1 : Math.Max(1, (int)((width + Spacing) / (MinCardWidth + Spacing)));

    private double CardWidth(double width, int columns) =>
        double.IsInfinity(width) ? MinCardWidth : (width - Spacing * (columns - 1)) / columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = ColumnsFor(availableSize.Width);
        double cardWidth = CardWidth(availableSize.Width, columns);
        double height = 0;
        for (int start = 0; start < Children.Count; start += columns)
        {
            double row = 0;
            for (int i = start; i < Math.Min(start + columns, Children.Count); i++)
            {
                Children[i].Measure(new Size(cardWidth, double.PositiveInfinity));
                row = Math.Max(row, Children[i].DesiredSize.Height);
            }
            height += row + (start > 0 ? Spacing : 0);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? cardWidth : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = ColumnsFor(finalSize.Width);
        double cardWidth = CardWidth(finalSize.Width, columns);
        double y = 0;
        for (int start = 0; start < Children.Count; start += columns)
        {
            int end = Math.Min(start + columns, Children.Count);
            double row = 0;
            for (int i = start; i < end; i++) row = Math.Max(row, Children[i].DesiredSize.Height);
            for (int i = start; i < end; i++) Children[i].Arrange(new Rect((i - start) * (cardWidth + Spacing), y, cardWidth, row));
            y += row + Spacing;
        }
        return finalSize;
    }
}

/// <summary>Children at their natural size, left to right, centered on each line and wrapping when the width runs out.</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += line + Spacing;
                x = 0;
                line = 0;
            }
            widest = Math.Max(widest, x + size.Width);
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
        }
        return new Size(widest, y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        int start = 0;
        while (start < Children.Count)
        {
            double width = 0, line = 0;
            int end = start;
            for (; end < Children.Count; end++)
            {
                var size = Children[end].DesiredSize;
                if (end > start && width + size.Width > finalSize.Width) break;
                width += size.Width + Spacing;
                line = Math.Max(line, size.Height);
            }

            double x = 0;
            for (int i = start; i < end; i++)
            {
                var size = Children[i].DesiredSize;
                Children[i].Arrange(new Rect(x, y + (line - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Spacing;
            }
            y += line + Spacing;
            start = end;
        }
        return finalSize;
    }
}
