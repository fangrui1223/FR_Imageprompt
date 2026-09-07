using PromptVault.App;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class GalleryVirtualizationTests
{
    [Theory]
    [InlineData(1800, 1900, 17, 1792)]
    [InlineData(1200, 1300, 24, 1192)]
    public void GalleryWidthUsesTheRealScrollViewportAndKeepsARightSafetyInset(
        double viewportWidth,
        double hostWidth,
        double scrollbarWidth,
        double expected)
    {
        Assert.Equal(
            expected,
            GalleryViewportWidthPolicy.Calculate(viewportWidth, hostWidth, scrollbarWidth));
    }

    [Fact]
    public void GalleryWidthFallbackReservesScrollbarBeforeFirstLayout()
    {
        var width = GalleryViewportWidthPolicy.Calculate(
            double.NaN,
            hostWidth: 1800,
            verticalScrollbarWidth: 18);

        Assert.Equal(1774, width);
    }

    [Theory]
    [InlineData(0, 0, 17)]
    [InlineData(double.NaN, 250, 17)]
    public void GalleryWidthNeverFallsBelowTheSupportedMinimum(
        double viewportWidth,
        double hostWidth,
        double scrollbarWidth)
    {
        Assert.Equal(
            GalleryViewportWidthPolicy.MinimumWidth,
            GalleryViewportWidthPolicy.Calculate(viewportWidth, hostWidth, scrollbarWidth));
    }

    [Theory]
    [InlineData(1000, 1500, "2:3  ·  0.667  ·  竖图")]
    [InlineData(1920, 1080, "16:9  ·  1.778  ·  横图")]
    [InlineData(1024, 1024, "1:1  ·  1  ·  方图")]
    [InlineData(0, 100, "宽高比未知")]
    public void AspectRatioSummaryIsReadableAndDeterministic(int width, int height, string expected)
    {
        Assert.Equal(expected, ImageAspectRatioFormatter.Format(width, height));
    }

    [Fact]
    public void PureImageCardsDoNotReserveAFormerLabelFooter()
    {
        var entry = CreateEntries(1)[0];
        var row = Assert.Single(GalleryLayoutEngine.CreateRows([entry], 900));
        var layout = Assert.Single(row.LayoutItems);
        var paths = new LibraryPaths(Path.Combine(Path.GetTempPath(), "PromptVaultVirtualizationTests"));

        row.Realize(paths, _ => false);
        var card = Assert.Single(row.Items);

        Assert.Equal(layout.ImageHeight, card.CardHeight);
        Assert.Equal(layout.ImageHeight, row.RowHeight);
    }

    [Fact]
    public void ThirtyThousandEntriesCreateNoCardViewModelsUntilRowsAreRealized()
    {
        var entries = CreateEntries(30_000);
        var rows = GalleryLayoutEngine.CreateRows(entries, 2500);

        Assert.All(rows, row => Assert.False(row.IsRealized));
        Assert.Equal(0, rows.Sum(row => row.Items.Count));

        var selectedId = entries[11].Id;
        var paths = new LibraryPaths(Path.Combine(Path.GetTempPath(), "PromptVaultVirtualizationTests"));
        foreach (var row in rows)
        {
            if (row.PanelY >= 1440 * 3) break;
            row.Realize(paths, id => id == selectedId);
        }

        var realizedCards = rows.Sum(row => row.Items.Count);
        Assert.InRange(realizedCards, 1, 200);
        Assert.True(rows.SelectMany(row => row.Items).Single(card => card.Id == selectedId).IsSelected);

        foreach (var row in rows) row.Release();
        Assert.Equal(0, rows.Sum(row => row.Items.Count));
    }

    [Fact]
    public void IncrementalPageLayoutMatchesOnePassLayout()
    {
        var entries = CreateEntries(3_017);
        const double width = 2500;
        var incremental = new List<GalleryRow>();
        for (var offset = 0; offset < entries.Count; offset += GalleryVirtualizationPolicy.PageSize)
        {
            var page = entries
                .Skip(offset)
                .Take(GalleryVirtualizationPolicy.PageSize)
                .ToArray();
            var append = GalleryLayoutEngine.CreateAppend(incremental, page, width);
            if (append.ReplaceIncompleteTail) incremental.RemoveAt(incremental.Count - 1);
            incremental.AddRange(append.Rows);
        }

        var expected = GalleryLayoutEngine.CreateRows(entries, width);
        Assert.Equal(expected.Count, incremental.Count);
        for (var rowIndex = 0; rowIndex < expected.Count; rowIndex++)
        {
            var expectedRow = expected[rowIndex];
            var actualRow = incremental[rowIndex];
            Assert.Equal(expectedRow.IsFilled, actualRow.IsFilled);
            Assert.Equal(
                expectedRow.LayoutItems.Select(item => item.Item.Id),
                actualRow.LayoutItems.Select(item => item.Item.Id));
            Assert.Equal(
                expectedRow.LayoutItems.Select(item => Math.Round(item.LayoutWidth, 6)),
                actualRow.LayoutItems.Select(item => Math.Round(item.LayoutWidth, 6)));
            Assert.Equal(
                expectedRow.LayoutItems.Select(item => Math.Round(item.LayoutX, 6)),
                actualRow.LayoutItems.Select(item => Math.Round(item.LayoutX, 6)));
            Assert.Equal(
                expectedRow.LayoutItems.Select(item => Math.Round(item.LayoutY, 6)),
                actualRow.LayoutItems.Select(item => Math.Round(item.LayoutY, 6)));
            Assert.Equal(expectedRow.ColumnIndex, actualRow.ColumnIndex);
            Assert.Equal(expectedRow.PanelX, actualRow.PanelX, 6);
            Assert.Equal(expectedRow.PanelY, actualRow.PanelY, 6);
            Assert.Equal(expectedRow.PanelWidth, actualRow.PanelWidth, 6);
        }
    }

    [Fact]
    public void WaterfallUsesOneContinuousShortestColumnState()
    {
        var entries = CreateEntries(40);
        var options = new GalleryLayoutOptions(GalleryLayoutMode.Waterfall, 14, 320);

        var rows = GalleryLayoutEngine.CreateRows(entries, 2500, options);

        Assert.Equal(entries.Count, rows.Count);
        Assert.All(rows, row => Assert.Single(row.LayoutItems));
        var columnCount = rows.Select(row => row.ColumnIndex).Distinct().Count();
        Assert.InRange(columnCount, 1, 12);
        var runningHeights = new double[columnCount];
        foreach (var row in rows)
        {
            var expectedColumn = Array.IndexOf(runningHeights, runningHeights.Min());
            Assert.Equal(expectedColumn, row.ColumnIndex);
            Assert.Equal(runningHeights[expectedColumn], row.PanelY, 6);
            runningHeights[expectedColumn] = row.PanelBottom + options.Spacing;
        }
    }

    [Fact]
    public void WaterfallNeverResetsAtFormerSectionBoundaries()
    {
        var entries = CreateEntries(120);
        var rows = GalleryLayoutEngine.CreateRows(
            entries,
            2500,
            new GalleryLayoutOptions(GalleryLayoutMode.Waterfall, 14, 320));

        var columnCount = rows.Select(row => row.ColumnIndex).Distinct().Count();
        var formerSectionSize = columnCount * 4;
        Assert.True(rows.Count > formerSectionSize * 2);
        Assert.True(rows[formerSectionSize].PanelY > 0);
        Assert.True(rows[formerSectionSize * 2].PanelY > rows[formerSectionSize].PanelY);
    }

    [Fact]
    public void ViewportIndexMatchesBruteForceAcrossLargeJumpsAndExtremeRatios()
    {
        var ratios = new (int Width, int Height)[]
        {
            (400, 1600),
            (900, 1600),
            (1000, 1500),
            (1200, 1600),
            (1200, 1200),
            (1600, 1200),
            (1600, 900),
            (1600, 400)
        };
        var entries = CreateEntries(1_200)
            .Select((item, index) => item with
            {
                Width = ratios[index % ratios.Length].Width,
                Height = ratios[index % ratios.Length].Height
            })
            .ToArray();
        var rows = GalleryLayoutEngine.CreateRows(
            entries,
            2500,
            new GalleryLayoutOptions(GalleryLayoutMode.Waterfall, 8, 320));
        var index = MasonryViewportIndex.Create(rows);
        const double viewportHeight = 1600;
        var scrollableHeight = Math.Max(0, index.ExtentHeight - viewportHeight);

        foreach (var fraction in new[] { 0d, 0.25d, 0.5d, 0.75d, 1d })
        {
            var top = scrollableHeight * fraction;
            var bottom = top + viewportHeight;
            var expected = rows
                .Select((row, ownerIndex) => (row, ownerIndex))
                .Where(pair => pair.row.PanelBottom >= top && pair.row.PanelY <= bottom)
                .Select(pair => pair.ownerIndex)
                .Order()
                .ToArray();

            Assert.Equal(expected, index.Query(top, bottom));
        }
    }

    [Fact]
    public void ViewportIndexRebuildUsesReflowedGeometry()
    {
        var entries = CreateEntries(500);
        var wideRows = GalleryLayoutEngine.CreateRows(entries, 2500);
        var narrowRows = GalleryLayoutEngine.CreateRows(entries, 760);
        var wide = MasonryViewportIndex.Create(wideRows);
        var narrow = MasonryViewportIndex.Create(narrowRows);

        Assert.NotEqual(wide.ColumnCount, narrow.ColumnCount);
        Assert.NotEqual(wide.ExtentHeight, narrow.ExtentHeight);
        Assert.Equal(
            narrowRows
                .Select((row, ownerIndex) => (row, ownerIndex))
                .Where(pair => pair.row.PanelBottom >= 3000 && pair.row.PanelY <= 4600)
                .Select(pair => pair.ownerIndex)
                .Order(),
            narrow.Query(3000, 4600));
    }

    [Theory]
    [InlineData(100, 400)]
    [InlineData(900, 1600)]
    [InlineData(200, 300)]
    [InlineData(300, 400)]
    [InlineData(500, 500)]
    [InlineData(400, 300)]
    [InlineData(1600, 900)]
    [InlineData(400, 100)]
    public void WaterfallPreservesTheFullOriginalAspectRatio(int width, int height)
    {
        var item = CreateEntries(1)[0] with { Width = width, Height = height };
        var row = Assert.Single(GalleryLayoutEngine.CreateRows([item], 400));

        Assert.Equal(row.PanelWidth * height / width, row.RowHeight, 6);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void InvalidMetadataUsesStableSquareGeometry(int width, int height)
    {
        var item = CreateEntries(1)[0] with { Width = width, Height = height };
        var row = Assert.Single(GalleryLayoutEngine.CreateRows([item], 400));

        Assert.True(double.IsFinite(row.RowHeight));
        Assert.Equal(row.PanelWidth, row.RowHeight, 6);
    }

    [Theory]
    [InlineData(120, 1)]
    [InlineData(600, 1)]
    [InlineData(2500, 7)]
    [InlineData(10000, 12)]
    public void WaterfallColumnCountStaysWithinOneToTwelve(
        double availableWidth,
        int expectedColumnCount)
    {
        var rows = GalleryLayoutEngine.CreateRows(CreateEntries(100), availableWidth);
        var columnCount = rows.Select(row => row.ColumnIndex).Distinct().Count();

        Assert.Equal(expectedColumnCount, columnCount);
        Assert.All(rows, row => Assert.True(row.PanelX + row.PanelWidth <= Math.Max(availableWidth, 320) + 0.001));
    }

    [Fact]
    public void JustifiedRowsKeepEqualImageHeights()
    {
        var entries = CreateEntries(18);
        var options = new GalleryLayoutOptions(GalleryLayoutMode.Justified, 8, 320);

        var rows = GalleryLayoutEngine.CreateRows(entries, 2500, options);

        Assert.True(rows.Count >= 2);
        foreach (var row in rows)
        {
            Assert.All(row.LayoutItems, item => Assert.Equal(0, item.LayoutY));
            Assert.Single(row.LayoutItems.Select(item => Math.Round(item.ImageHeight, 6)).Distinct());
        }
    }

    [Fact]
    public void SameStableRowsReuseCardViewModelsAndRefreshMetadata()
    {
        var entries = CreateEntries(7);
        var paths = new LibraryPaths(Path.Combine(Path.GetTempPath(), "PromptVaultVirtualizationTests"));
        var current = GalleryLayoutEngine.CreateRows(entries, 2500);
        current[0].Realize(paths, _ => false);
        var originalCards = current[0].Items.ToArray();

        var updatedEntries = entries
            .Select(item => item with
            {
                CategoryName = $"updated-{item.Id}",
                Tags = $"tag-{item.Id}",
                Prompt = $"updated prompt {item.Id}"
            })
            .ToArray();
        var updated = GalleryLayoutEngine.CreateRows(updatedEntries, 2500);

        Assert.True(current[0].CanReuseFrom(updated[0]));
        current[0].UpdateFrom(updated[0], id => id == entries[0].Id);

        Assert.Equal(originalCards.Length, current[0].Items.Count);
        for (var index = 0; index < originalCards.Length; index++)
        {
            Assert.Same(originalCards[index], current[0].Items[index]);
            Assert.Equal($"updated-{current[0].Items[index].Id}", current[0].Items[index].CategoryName);
            Assert.Equal($"tag-{current[0].Items[index].Id}", current[0].Items[index].Tags);
        }
        Assert.True(current[0].Items[0].IsSelected);
    }

    [Fact]
    public void ReflowedRowsTransferReusableCardsByStableId()
    {
        var entries = CreateEntries(18);
        var paths = new LibraryPaths(Path.Combine(Path.GetTempPath(), "PromptVaultVirtualizationTests"));
        var current = GalleryLayoutEngine.CreateRows(entries, 2500);
        foreach (var row in current) row.Realize(paths, _ => false);
        var reusable = current
            .SelectMany(row => row.Items)
            .ToDictionary(card => card.Id);

        var reflowed = GalleryLayoutEngine.CreateRows(entries, 900);
        var transferred = new HashSet<GalleryCardViewModel>();
        foreach (var row in reflowed)
        {
            row.Realize(paths, _ => false, reusable, transferred);
        }

        Assert.Equal(reusable.Count, transferred.Count);
        foreach (var card in reflowed.SelectMany(row => row.Items))
        {
            Assert.Same(reusable[card.Id], card);
        }

        foreach (var row in current) row.Release(transferred);
        Assert.All(current, row => Assert.False(row.IsRealized));
        Assert.Equal(reusable.Count, reflowed.Sum(row => row.Items.Count));
    }

    [Fact]
    public void ChangedThumbnailIdentityDoesNotReuseOldCard()
    {
        var entry = CreateEntries(1)[0];
        var paths = new LibraryPaths(Path.Combine(Path.GetTempPath(), "PromptVaultVirtualizationTests"));
        var current = GalleryLayoutEngine.CreateRows([entry], 900);
        current[0].Realize(paths, _ => false);
        var oldCard = current[0].Items[0];
        var changed = entry with
        {
            Hash = "changed-hash",
            ThumbnailPath = "thumbnails/changed.jpg",
            MediumThumbnailPath = "thumbnails/medium/changed.jpg"
        };
        var desired = GalleryLayoutEngine.CreateRows([changed], 900);
        var transferred = new HashSet<GalleryCardViewModel>();

        desired[0].Realize(
            paths,
            _ => false,
            new Dictionary<long, GalleryCardViewModel> { [oldCard.Id] = oldCard },
            transferred);

        Assert.NotSame(oldCard, desired[0].Items[0]);
        Assert.Empty(transferred);
    }

    [Theory]
    [InlineData(0, 1000, 0, false)]
    [InlineData(1000, 0, 0, true)]
    [InlineData(1000, 10_000, 8_000, true)]
    [InlineData(1000, 10_000, 7_000, false)]
    public void PrefetchPolicyOnlyLoadsWithinTwoViewports(
        double viewportHeight,
        double scrollableHeight,
        double verticalOffset,
        bool expected)
    {
        Assert.Equal(
            expected,
            GalleryVirtualizationPolicy.ShouldPrefetch(
                viewportHeight,
                scrollableHeight,
                verticalOffset));
    }

    private static List<GalleryEntry> CreateEntries(int count)
    {
        var start = DateTimeOffset.UtcNow.AddDays(-count);
        return Enumerable.Range(1, count)
            .Select(index => new GalleryEntry(
                index,
                $"hash-{index}",
                $"originals/{index}.jpg",
                $"thumbnails/{index}.jpg",
                $"thumbnails/medium/{index}.jpg",
                640 + index % 1800,
                640 + index % 1200,
                "jpg",
                $"prompt {index}",
                "",
                null,
                "",
                "",
                start.AddMinutes(index),
                null,
                false,
                null))
            .ToList();
    }
}
