using System.Windows;
using Albumen.App.Review;
using Albumen.App.Shell;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class ReviewDragDataTests
{
    private static readonly Guid SetId = Guid.Parse("31000000-0000-0000-0000-000000000001");

    [Fact]
    public void RoundTripsTheOccurrenceAndItsSet()
    {
        var data = ReviewDragData.Create(42, SetId);

        Assert.True(ReviewDragData.TryRead(data, out var occurrenceId, out var groupId, out var fromViewer));
        Assert.Equal(42, occurrenceId);
        Assert.Equal(SetId, groupId);
        Assert.Null(fromViewer);
        Assert.True(data.GetDataPresent(ReviewDragData.Format));
    }

    [Fact]
    public void RoundTripsTheViewerAPhotoIsDraggedFrom()
    {
        var data = ReviewDragData.Create(7, SetId, ViewerSide.Right);

        Assert.True(ReviewDragData.TryRead(data, out var occurrenceId, out var groupId, out var fromViewer));
        Assert.Equal(7, occurrenceId);
        Assert.Equal(SetId, groupId);
        Assert.Equal(ViewerSide.Right, fromViewer);
    }

    [Fact]
    public void RejectsOtherData()
    {
        Assert.False(ReviewDragData.TryRead(new DataObject(DataFormats.Text, "C:\\Photos\\1.jpg"), out _, out _, out _));
        Assert.False(ReviewDragData.TryRead(new DataObject(ReviewDragData.Format, "not a photo"), out _, out _, out _));
        Assert.False(ReviewDragData.TryRead(null, out _, out _, out _));
    }
}
