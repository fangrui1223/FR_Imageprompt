using PromptVault.App;

namespace PromptVault.Tests;

public sealed class GallerySelectionNavigatorTests
{
    [Fact]
    public void RangeSelectionUsesStableGalleryOrderInEitherDirection()
    {
        long[] ordered = [10, 20, 30, 40, 50];

        Assert.Equal([20, 30, 40], GallerySelectionNavigator.Range(ordered, 20, 40));
        Assert.Equal([20, 30, 40], GallerySelectionNavigator.Range(ordered, 40, 20));
    }

    [Fact]
    public void KeyboardNavigationRespectsVisualRowsAndClampsShortRows()
    {
        IReadOnlyList<IReadOnlyList<long>> rows =
        [
            new long[] { 1, 2, 3 },
            new long[] { 4, 5 },
            new long[] { 6, 7, 8, 9 }
        ];

        Assert.Equal(5, GallerySelectionNavigator.Move(rows, 3, GalleryNavigationDirection.Down));
        Assert.Equal(2, GallerySelectionNavigator.Move(rows, 5, GalleryNavigationDirection.Up));
        Assert.Equal(6, GallerySelectionNavigator.Move(rows, 5, GalleryNavigationDirection.Right));
        Assert.Equal(1, GallerySelectionNavigator.Move(rows, 7, GalleryNavigationDirection.Home));
        Assert.Equal(9, GallerySelectionNavigator.Move(rows, 7, GalleryNavigationDirection.End));
    }

    [Fact]
    public void KeyboardNavigationUsesCardGeometryForWaterfallColumns()
    {
        GalleryCardPosition[] positions =
        [
            new(1, 0, 0, 100, 180),
            new(2, 114, 0, 100, 120),
            new(3, 114, 134, 100, 160),
            new(4, 0, 194, 100, 120)
        ];

        Assert.Equal(2, GallerySelectionNavigator.Move(positions, 1, GalleryNavigationDirection.Right));
        Assert.Equal(3, GallerySelectionNavigator.Move(positions, 2, GalleryNavigationDirection.Down));
        Assert.Equal(4, GallerySelectionNavigator.Move(positions, 3, GalleryNavigationDirection.Left));
        Assert.Equal(1, GallerySelectionNavigator.Move(positions, 4, GalleryNavigationDirection.Up));
    }
}
