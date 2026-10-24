using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.App.Review;
using Albumen.App.Shell;

namespace Albumen.App;

// True when both bound values are equal; used to mark the open kind, set and photo.
public sealed class EqualValuesConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [var left, var right]
            && left is not null && left != DependencyProperty.UnsetValue
            && right is not null && right != DependencyProperty.UnsetValue
            && Equals(left, right);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// A Review photo's thumbnail. It asks the shared loader when it is shown and cancels when the
// photo scrolls away or is recycled for another photo, so only visible photos decode. In the
// gallery (LoadsWhenInView) a set may be far taller than the view, so a thumbnail waits until
// it is within a row of the gallery's visible area. It reports its photo's shape to the
// JustifiedPanel that lays the set out.
public sealed class PhotoThumbnail : Border
{
    public static readonly DependencyProperty MemberProperty = DependencyProperty.Register(
        nameof(Member), typeof(ScanOccurrenceViewModel), typeof(PhotoThumbnail),
        new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty LoaderProperty = DependencyProperty.Register(
        nameof(Loader), typeof(ReviewThumbnailLoader), typeof(PhotoThumbnail),
        new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty LoadsWhenInViewProperty = DependencyProperty.Register(
        nameof(LoadsWhenInView), typeof(bool), typeof(PhotoThumbnail), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey DimensionsTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DimensionsText), typeof(string), typeof(PhotoThumbnail), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DimensionsTextProperty = DimensionsTextPropertyKey.DependencyProperty;

    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock status = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6),
    };
    private CancellationTokenSource? loading;
    private ScrollViewer? viewport;
    private bool inView = true;
    private bool shown;

    public PhotoThumbnail()
    {
        ClipToBounds = true;
        Child = new Grid { Children = { status, image } };
        Loaded += (_, _) =>
        {
            WatchViewport();
            Reload();
        };
        Unloaded += (_, _) =>
        {
            UnwatchViewport();
            Cancel();
        };
    }

    public bool LoadsWhenInView
    {
        get => (bool)GetValue(LoadsWhenInViewProperty);
        set => SetValue(LoadsWhenInViewProperty, value);
    }

    public ScanOccurrenceViewModel? Member
    {
        get => (ScanOccurrenceViewModel?)GetValue(MemberProperty);
        set => SetValue(MemberProperty, value);
    }

    public ReviewThumbnailLoader? Loader
    {
        get => (ReviewThumbnailLoader?)GetValue(LoaderProperty);
        set => SetValue(LoaderProperty, value);
    }

    public string DimensionsText => (string)GetValue(DimensionsTextProperty);

    private static void OnSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs eventArgs) =>
        ((PhotoThumbnail)target).Reload();

    private async void Reload()
    {
        Cancel();
        shown = false;
        image.Source = null;
        status.Text = string.Empty;
        SetValue(DimensionsTextPropertyKey, string.Empty);
        if (!IsLoaded || !inView || Member is not { } member || Loader is not { } loader) return;

        var cancellation = new CancellationTokenSource();
        loading = cancellation;
        try
        {
            var thumbnail = await loader.LoadAsync(member, cancellation.Token);
            if (!ReferenceEquals(loading, cancellation) || thumbnail is null) return;
            shown = true;
            image.Source = thumbnail.Image;
            status.Text = thumbnail.Image is null ? "No preview" : string.Empty;
            SetValue(DimensionsTextPropertyKey, thumbnail.DimensionsText);
            if (thumbnail.Image is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
                JustifiedPanel.ReportAspect(this, (double)bitmap.PixelWidth / bitmap.PixelHeight);
        }
        catch (Exception)
        {
            // A thumbnail is decoration on a card that already names the photo; a failed
            // decode must never take the window down.
            if (ReferenceEquals(loading, cancellation)) status.Text = "No preview";
        }
        finally
        {
            if (ReferenceEquals(loading, cancellation)) loading = null;
            cancellation.Dispose();
        }
    }

    private void Cancel()
    {
        var current = loading;
        loading = null;
        current?.Cancel();
    }

    private void WatchViewport()
    {
        UnwatchViewport();
        if (!LoadsWhenInView) return;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroller) continue;
            viewport = scroller;
            scroller.ScrollChanged += OnViewportScrollChanged;
            break;
        }
        inView = IsInViewport();
    }

    private void UnwatchViewport()
    {
        if (viewport is not null) viewport.ScrollChanged -= OnViewportScrollChanged;
        viewport = null;
        inView = true;
    }

    // Scrolling, a set realized or re-flowed, or a resized gallery: a photo that came into view
    // loads, one that left it stops a pending load (a loaded thumbnail stays).
    private void OnViewportScrollChanged(object sender, ScrollChangedEventArgs eventArgs)
    {
        var now = IsInViewport();
        if (now == inView) return;
        inView = now;
        if (!now) Cancel();
        else if (!shown && loading is null) Reload();
    }

    // Within the visible area, or one row (this photo's height) above or below it.
    private bool IsInViewport()
    {
        if (viewport is not { } scroller) return true;
        if (!IsVisible || ActualHeight <= 0 || !scroller.IsAncestorOf(this)) return false;
        var bounds = TransformToAncestor(scroller).TransformBounds(new Rect(RenderSize));
        return bounds.Bottom >= -bounds.Height && bounds.Top <= scroller.ActualHeight + bounds.Height;
    }

    // The photo's corners are rounded as the thumbnail's own CornerRadius says.
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var radius = CornerRadius.TopLeft;
        if (radius <= 0) return;
        var clip = new RectangleGeometry(new Rect(RenderSize), radius, radius);
        clip.Freeze();
        Clip = clip;
    }

    // The card's open button already carries the photo's name, so the picture itself is
    // left out of the automation tree.
    protected override AutomationPeer OnCreateAutomationPeer() => new PhotoThumbnailPeer(this);

    private sealed class PhotoThumbnailPeer(PhotoThumbnail owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetChildrenCore() => [];
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}

// One of two styles by a flag: a viewer's Remove button is red while its photo is marked.
public sealed class BooleanToStyleConverter : IValueConverter
{
    public Style? TrueStyle { get; set; }
    public Style? FalseStyle { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true ? TrueStyle : FalseStyle) ?? DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Visible when both bound values are equal; shows the "Suggested best" badge on one card.
public sealed class EqualValuesToVisibilityConverter : IMultiValueConverter
{
    private readonly EqualValuesConverter equal = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        equal.Convert(values, typeof(bool), parameter, culture) is true ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
