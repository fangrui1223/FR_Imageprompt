using System.Windows;
using PromptVault.App;
using PromptVault.App.Services;

namespace PromptVault.Tests;

public sealed class BoardTransferPolicyTests
{
    [Fact]
    public void MultiSelectionKeepsManagedItemsOnceAndReportsExternalItems()
    {
        var active = Entry(10);
        var deleted = Entry(11) with { DeletedAt = DateTimeOffset.UtcNow };
        var external = Entry(-12) with { IsExternal = true, ExternalFolderId = "external" };

        var selection = BoardTransferPolicy.Select([active, active, deleted, external]);

        Assert.Equal([10L], selection.ManagedItems.Select(item => item.CollectionItemId));
        Assert.Equal(1, selection.ExternalItemCount);
        Assert.Equal((1600, 900), (selection.ManagedItems[0].Width, selection.ManagedItems[0].Height));
    }

    [Fact]
    public void BoardDragPayloadRoundTripsManagedIdsInProcess()
    {
        var data = new DataObject();
        var ids = new[] { 4L, 8L, 15L, 16L, 23L, 42L };
        data.SetData(BoardWindow.BoardItemIdsDragFormat, ids, false);

        Assert.True(data.GetDataPresent(BoardWindow.BoardItemIdsDragFormat, false));
        Assert.Equal(ids, Assert.IsType<long[]>(
            data.GetData(BoardWindow.BoardItemIdsDragFormat, false)));
    }

    private static GalleryEntry Entry(long id) => new(
        id,
        $"hash-{id}",
        $"originals/{id}.png",
        $"small/{id}.jpg",
        $"medium/{id}.jpg",
        1600,
        900,
        "png",
        "prompt",
        "",
        null,
        "未分类",
        "",
        DateTimeOffset.UtcNow,
        null,
        false,
        null);
}
