using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Albumen.App.Review;

// The small picture of a photo being dragged, on the window's adorner layer just below and right
// of the pointer. It takes no hit test, so the viewer under the pointer still gets the drop.
internal sealed class ReviewDragImage : Adorner
{
    // The picture's longest edge, in device-independent pixels.
    internal const double Edge = 96;

    private readonly Border frame;
    private Point pointer;

    internal ReviewDragImage(UIElement root, ImageSource picture) : base(root)
    {
        ArgumentNullException.ThrowIfNull(picture);
        Picture = picture;
        IsHitTestVisible = false;
        var longest = Math.Max(picture.Width, picture.Height);
        PictureSize = longest > 0
            ? new Size(picture.Width * Edge / longest, picture.Height * Edge / longest)
            : new Size(Edge, Edge);
        frame = new Border
        {
            Width = PictureSize.Width,
            Height = PictureSize.Height,
            CornerRadius = new CornerRadius(6),
            Opacity = 0.85,
            IsHitTestVisible = false,
            Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
        };
        AddVisualChild(frame);
    }

    // Where the picture's top left sits from the pointer's tip.
    internal static Vector Offset { get; } = new(12, 12);

    internal ImageSource Picture { get; }

    internal Size PictureSize { get; }

    // The picture's top left, in the coordinates of the element the layer adorns.
    internal Point Position => pointer + Offset;

    internal void MoveTo(Point pointerInRoot)
    {
        pointer = pointerInRoot;
        InvalidateArrange();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? frame : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size ArrangeOverride(Size finalSize)
    {
        frame.Arrange(new Rect(Position, PictureSize));
        return finalSize;
    }
}
