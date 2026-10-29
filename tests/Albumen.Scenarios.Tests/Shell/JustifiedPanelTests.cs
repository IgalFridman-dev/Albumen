using System.Windows;
using System.Windows.Controls;
using Albumen.App.Review;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// 1.1.5 gallery: rows of equal height, each photo as wide as its shape needs (Windows Photos).
public sealed class JustifiedPanelTests
{
    private const double Landscape = 4.0 / 3.0;
    private const double Portrait = 3.0 / 4.0;
    private const double Wide = 16.0 / 9.0;

    [Fact]
    public void ThreeLandscapePhotosFillOneRow()
    {
        var tiles = JustifiedPanel.Layout([Landscape, Landscape, Landscape], 900, 200);

        Assert.All(tiles, tile => Assert.Equal(0, tile.Top));
        Assert.All(tiles, tile => Assert.Equal(tiles[0].Height, tile.Height, 3));
        Assert.Equal(900 - 2 * JustifiedPanel.Gap, tiles.Sum(tile => tile.Width), 3);
        Assert.Equal(900, tiles[2].Right, 3);
    }

    [Fact]
    public void SixPortraitPhotosFillOneRow()
    {
        var tiles = JustifiedPanel.Layout(Enumerable.Repeat(Portrait, 6).ToArray(), 900, 200);

        Assert.All(tiles, tile => Assert.Equal(0, tile.Top));
        Assert.All(tiles, tile => Assert.Equal(tiles[0].Height, tile.Height, 3));
        Assert.Equal(900, tiles[5].Right, 3);
    }

    [Fact]
    public void SixLandscapePhotosWrapIntoTwoRowsOfThree()
    {
        var tiles = JustifiedPanel.Layout(Enumerable.Repeat(Landscape, 6).ToArray(), 900, 200);

        Assert.All(tiles[..3], tile => Assert.Equal(0, tile.Top));
        var secondRow = tiles[0].Height + JustifiedPanel.Gap;
        Assert.All(tiles[3..], tile => Assert.Equal(secondRow, tile.Top, 3));
        Assert.Equal(0, tiles[3].Left);
        Assert.Equal(900, tiles[2].Right, 3);
        Assert.Equal(900, tiles[5].Right, 3);
    }

    [Fact]
    public void ASinglePhotoStaysLeftAlignedAtTheRowHeight()
    {
        var tile = Assert.Single(JustifiedPanel.Layout([Landscape], 900, 200));

        Assert.Equal(new Rect(0, 0, 200 * Landscape, 200), tile);
    }

    [Fact]
    public void APanoramaAloneOnARowGrowsNoMoreThanAQuarterAndStaysLeftAligned()
    {
        // The second panorama does not fit beside the first, so the first is alone on its row.
        var tiles = JustifiedPanel.Layout([3.0, 3.0], 900, 200);

        Assert.Equal(new Rect(0, 0, 750, 250), tiles[0]);
        Assert.Equal(new Rect(0, 250 + JustifiedPanel.Gap, 600, 200), tiles[1]);
    }

    [Fact]
    public void ChangingOnePhotosShapeLaysTheRowOutAgain()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var panel = new JustifiedPanel { RowHeight = 200 };
            var children = Enumerable.Range(0, 3).Select(_ => new Border()).ToArray();
            foreach (var child in children) panel.Children.Add(child);
            var window = new Window { Content = panel, Width = 900, Height = 600, SizeToContent = SizeToContent.Manual };
            try
            {
                window.Show();
                panel.Width = 900;
                panel.UpdateLayout();
                Assert.Equal(296, children[0].ActualWidth, 1);

                // The middle photo's thumbnail reports a 16:9 photo.
                JustifiedPanel.SetAspect(children[1], Wide);
                Assert.False(panel.IsMeasureValid);
                panel.UpdateLayout();

                var height = (900 - 2 * JustifiedPanel.Gap) / (2 * Landscape + Wide);
                Assert.All(children, child => Assert.Equal(height, child.ActualHeight, 1));
                Assert.Equal(Wide * height, children[1].ActualWidth, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
