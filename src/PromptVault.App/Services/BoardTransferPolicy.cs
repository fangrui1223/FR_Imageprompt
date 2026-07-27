namespace PromptVault.App.Services;

internal sealed record BoardTransferSelection(
    IReadOnlyList<BoardAddItem> ManagedItems,
    int ExternalItemCount);

internal static class BoardTransferPolicy
{
    public static BoardTransferSelection Select(IReadOnlyList<GalleryEntry> entries)
    {
        var externalCount = entries.Count(entry => entry.IsExternal);
        var managed = entries
            .Where(entry => !entry.IsExternal && entry.DeletedAt is null)
            .GroupBy(entry => entry.Id)
            .Select(group =>
            {
                var entry = group.First();
                return new BoardAddItem(entry.Id, entry.Width, entry.Height);
            })
            .ToArray();
        return new BoardTransferSelection(managed, externalCount);
    }
}
