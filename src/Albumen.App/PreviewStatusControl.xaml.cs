using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Albumen.App;

public partial class PreviewStatusControl : UserControl
{
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(PreviewStatusControl),
        new PropertyMetadata(1.0, OnSizingChanged));

    public static readonly DependencyProperty FitHeightProperty = DependencyProperty.Register(
        nameof(FitHeight), typeof(double), typeof(PreviewStatusControl),
        new PropertyMetadata(512.0, OnSizingChanged));

    public PreviewStatusControl()
    {
        InitializeComponent();
        PreviewImageScroller.SizeChanged += (_, eventArgs) =>
        {
            if (eventArgs.WidthChanged) UpdateImageBounds();
        };
    }

    // 1 fits the photo to the available width and FitHeight; larger values enlarge it, and the
    // photo then scrolls both ways inside a box that keeps FitHeight.
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double FitHeight
    {
        get => (double)GetValue(FitHeightProperty);
        set => SetValue(FitHeightProperty, value);
    }

    private static void OnSizingChanged(DependencyObject owner, DependencyPropertyChangedEventArgs eventArgs) =>
        ((PreviewStatusControl)owner).UpdateImageBounds();

    private void UpdateImageBounds()
    {
        var zoom = Math.Clamp(Zoom, 1, 8);
        var fit = Math.Max(1, FitHeight);
        PreviewImage.MaxHeight = fit * zoom;
        var width = PreviewImageScroller.ActualWidth;
        PreviewImage.MaxWidth = width > 0 ? width * zoom : double.PositiveInfinity;
        // A fitted photo needs no scroll bar; a zoomed one shows both, so every part of it can be
        // reached without the box growing past FitHeight.
        PreviewImageScroller.MaxHeight = fit;
        var bars = zoom > 1 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PreviewImageScroller.HorizontalScrollBarVisibility = bars;
        PreviewImageScroller.VerticalScrollBarVisibility = bars;
    }

    // The wheel scrolls a zoomed photo up and down until it reaches its top or bottom; then, and
    // over a photo that fits, the wheel scrolls the page.
    private void OnPreviewImageMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (eventArgs.Handled) return;
        var scroller = PreviewImageScroller;
        if (eventArgs.Delta > 0 ? scroller.VerticalOffset > 0 : scroller.VerticalOffset < scroller.ScrollableHeight)
            return;
        eventArgs.Handled = true;
        RaiseEvent(new MouseWheelEventArgs(eventArgs.MouseDevice, eventArgs.Timestamp, eventArgs.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = this,
        });
    }
}
