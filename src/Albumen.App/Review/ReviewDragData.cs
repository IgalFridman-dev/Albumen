using System.Globalization;
using System.Windows;
using Albumen.App.Shell;

namespace Albumen.App.Review;

// What a dragged photo carries: its set, the photo and, when it comes from a viewer, that
// viewer. The viewers accept only this format, so nothing dragged in from outside places a photo.
internal static class ReviewDragData
{
    internal const string Format = "Albumen.ReviewPhoto";

    internal static DataObject Create(long occurrenceId, Guid groupId, ViewerSide? fromViewer = null) =>
        new(Format, string.Create(CultureInfo.InvariantCulture, $"{groupId:D}|{occurrenceId}|{fromViewer}"));

    internal static bool TryRead(IDataObject? data, out long occurrenceId, out Guid groupId, out ViewerSide? fromViewer)
    {
        occurrenceId = 0;
        groupId = Guid.Empty;
        fromViewer = null;
        if (data is null || !data.GetDataPresent(Format) || data.GetData(Format) is not string text) return false;
        var parts = text.Split('|');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "D", out groupId)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out occurrenceId))
            return false;
        if (parts[2].Length == 0) return true;
        if (!Enum.TryParse(parts[2], out ViewerSide side) || !Enum.IsDefined(side)) return false;
        fromViewer = side;
        return true;
    }
}
