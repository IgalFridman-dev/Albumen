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

    // 1 fits the photo to the available width and FitHeight; larger values enlarge it
    // and the photo then pans sideways while the page scrolls vertically.
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
        PreviewImage.MaxHeight = Math.Max(1, FitHeight) * zoom;
        var width = PreviewImageScroller.ActualWidth;
        PreviewImage.MaxWidth = width > 0 ? width * zoom : double.PositiveInfinity;
    }

    // The image scroller only pans sideways, so the wheel keeps scrolling the page.
    private void OnPreviewImageMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (eventArgs.Handled) return;
        eventArgs.Handled = true;
        RaiseEvent(new MouseWheelEventArgs(eventArgs.MouseDevice, eventArgs.Timestamp, eventArgs.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = this,
        });
    }
}
