using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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

// A Review photo card's thumbnail. It asks the shared loader when it is shown and cancels
// when the card scrolls away or is recycled for another photo, so only visible cards decode.
public sealed class PhotoThumbnail : Border
{
    public static readonly DependencyProperty MemberProperty = DependencyProperty.Register(
        nameof(Member), typeof(ScanOccurrenceViewModel), typeof(PhotoThumbnail),
        new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty LoaderProperty = DependencyProperty.Register(
        nameof(Loader), typeof(ReviewThumbnailLoader), typeof(PhotoThumbnail),
        new PropertyMetadata(null, OnSourceChanged));

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

    public PhotoThumbnail()
    {
        ClipToBounds = true;
        Child = new Grid { Children = { status, image } };
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => Cancel();
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
        image.Source = null;
        status.Text = string.Empty;
        SetValue(DimensionsTextPropertyKey, string.Empty);
        if (!IsLoaded || Member is not { } member || Loader is not { } loader) return;

        var cancellation = new CancellationTokenSource();
        loading = cancellation;
        try
        {
            var thumbnail = await loader.LoadAsync(member, cancellation.Token);
            if (!ReferenceEquals(loading, cancellation) || thumbnail is null) return;
            image.Source = thumbnail.Image;
            status.Text = thumbnail.Image is null ? "No preview" : string.Empty;
            SetValue(DimensionsTextPropertyKey, thumbnail.DimensionsText);
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

// Visible when both bound values are equal; shows the "Suggested best" badge on one card.
public sealed class EqualValuesToVisibilityConverter : IMultiValueConverter
{
    private readonly EqualValuesConverter equal = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        equal.Convert(values, typeof(bool), parameter, culture) is true ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
