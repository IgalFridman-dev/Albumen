using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Albumen.App.Review;

// Lays a set's photos out like the Windows Photos gallery: rows of equal height, each photo as
// wide as its shape needs. A row takes photos while they fit the width at RowHeight, then grows
// to fill the width exactly, up to 1.25 x RowHeight (a wider row stays left-aligned). The last
// row fills the width only when that keeps it under the same cap; otherwise it keeps RowHeight.
// A child's shape is its attached Aspect (width / height), 4:3 until its thumbnail reports it.
public sealed class JustifiedPanel : Panel
{
    internal const double Gap = 6;
    internal const double MaximumGrowth = 1.25;
    private const double DefaultAspect = 4.0 / 3.0;

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(JustifiedPanel),
        new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty AspectProperty = DependencyProperty.RegisterAttached(
        "Aspect", typeof(double), typeof(JustifiedPanel),
        new FrameworkPropertyMetadata(DefaultAspect, OnAspectChanged));

    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public static double GetAspect(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(AspectProperty);
    }

    public static void SetAspect(UIElement element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(AspectProperty, value);
    }

    // Only the rows of this panel move; the gallery re-flows one set at a time.
    private static void OnAspectChanged(DependencyObject element, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (element is Visual visual && VisualTreeHelper.GetParent(visual) is JustifiedPanel panel)
            panel.InvalidateMeasure();
    }

    // A thumbnail sits inside a template, so it reports its photo's shape to the panel's child
    // that holds it (the item container), which is where the panel reads it.
    internal static void ReportAspect(DependencyObject element, double aspect)
    {
        for (var child = element; VisualTreeHelper.GetParent(child) is { } parent; child = parent)
        {
            if (parent is not JustifiedPanel) continue;
            if (child is UIElement item) SetAspect(item, aspect);
            return;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var tiles = Layout(Aspects(), availableSize.Width, RowHeight);
        var size = new Size();
        for (var index = 0; index < tiles.Length; index++)
        {
            InternalChildren[index].Measure(tiles[index].Size);
            size.Width = Math.Max(size.Width, tiles[index].Right);
            size.Height = Math.Max(size.Height, tiles[index].Bottom);
        }
        if (!double.IsInfinity(availableSize.Width)) size.Width = availableSize.Width;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tiles = Layout(Aspects(), finalSize.Width, RowHeight);
        for (var index = 0; index < tiles.Length; index++)
            InternalChildren[index].Arrange(tiles[index]);
        return finalSize;
    }

    internal static Rect[] Layout(IReadOnlyList<double> aspects, double width, double rowHeight)
    {
        ArgumentNullException.ThrowIfNull(aspects);
        var tiles = new Rect[aspects.Count];
        // Without a width to fill, the photos stand in one row at RowHeight.
        var fill = width > 0 && !double.IsInfinity(width);
        var top = 0.0;
        var start = 0;
        var shape = 0.0;
        for (var index = 0; index < aspects.Count; index++)
        {
            var aspect = Sane(aspects[index]);
            if (fill && index > start && (shape + aspect) * rowHeight > width + 0.5)
            {
                top = PlaceRow(tiles, aspects, start, index, shape, width, rowHeight, top, last: false) + Gap;
                start = index;
                shape = 0;
            }
            shape += aspect;
        }
        if (start < aspects.Count)
            PlaceRow(tiles, aspects, start, aspects.Count, shape, fill ? width : double.PositiveInfinity,
                rowHeight, top, last: true);
        return tiles;
    }

    private static double PlaceRow(Rect[] tiles, IReadOnlyList<double> aspects, int start, int end,
        double shape, double width, double rowHeight, double top, bool last)
    {
        var filled = (width - (end - start - 1) * Gap) / shape;
        var height = filled <= rowHeight * MaximumGrowth
            ? filled
            : last ? rowHeight : rowHeight * MaximumGrowth;
        var left = 0.0;
        for (var index = start; index < end; index++)
        {
            var tileWidth = Sane(aspects[index]) * height;
            tiles[index] = new Rect(left, top, tileWidth, height);
            left += tileWidth + Gap;
        }
        return top + height;
    }

    private static double Sane(double aspect) =>
        aspect > 0 && !double.IsInfinity(aspect) ? aspect : DefaultAspect;

    private double[] Aspects()
    {
        var aspects = new double[InternalChildren.Count];
        for (var index = 0; index < aspects.Length; index++)
            aspects[index] = GetAspect(InternalChildren[index]);
        return aspects;
    }
}
