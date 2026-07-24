using PromptVault.App;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class GalleryVirtualizationTests
{
    [Fact]
    public void ThirtyThousandEntriesCreateNoCardViewModelsUntilRowsAreRealized()
    {
        var entries = CreateEntries(30_000);
        var rows = GalleryLayoutEngine.CreateRows(entries, 2500);

        Assert.All(rows, row => Assert.False(row.IsRealized));
        Assert.Equal(0, rows.Sum(row => row.Items.Count));

        var selectedId = entries[11].Id;
        var paths = new LibraryPaths(Path.Combine(Path.GetTempPath(), "PromptVaultVirtualizationTests"));
        var coveredHeight = 0d;
        foreach (var row in rows)
        {
            row.Realize(paths, id => id == selectedId);
            coveredHeight += row.LayoutItems.Max(item => item.ImageHeight) + 56;
            if (coveredHeight >= 1440 * 3) break;
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
