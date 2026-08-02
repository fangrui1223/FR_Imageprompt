namespace PromptVault.Core;

public enum BoardInputPriority
{
    NormalCanvas,
    FocusNavigation,
    Overlay,
    Transform,
    TextEditing,
    Modal
}

public static class BoardInteractionEngine
{
    public const double DefaultDragThreshold = 5;

    public static BoardInputPriority ResolvePriority(
        bool hasModal,
        bool isTextEditing,
        bool isTransforming,
        bool hasOverlay,
        bool isFocused) =>
        hasModal ? BoardInputPriority.Modal
        : isTextEditing ? BoardInputPriority.TextEditing
        : isTransforming ? BoardInputPriority.Transform
        : hasOverlay ? BoardInputPriority.Overlay
        : isFocused ? BoardInputPriority.FocusNavigation
        : BoardInputPriority.NormalCanvas;

    public static bool ExceedsDragThreshold(
        double startX,
        double startY,
        double currentX,
        double currentY,
        double threshold = DefaultDragThreshold)
    {
        threshold = double.IsFinite(threshold) ? Math.Max(0, threshold) : DefaultDragThreshold;
        var dx = currentX - startX;
        var dy = currentY - startY;
        return double.IsFinite(dx)
            && double.IsFinite(dy)
            && dx * dx + dy * dy >= threshold * threshold;
    }

    public static IReadOnlySet<long> SelectItem(
        IReadOnlyList<BoardItemRecord> items,
        IReadOnlySet<long> currentSelection,
        long clickedItemId,
        bool additive)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(currentSelection);
        var clicked = items.SingleOrDefault(item => item.Id == clickedItemId);
        if (clicked is null) return currentSelection.ToHashSet();
        var target = clicked.GroupId is { } groupId
            ? items.Where(item => item.GroupId == groupId).Select(item => item.Id).ToHashSet()
            : new HashSet<long> { clickedItemId };
        if (!additive)
        {
            return currentSelection.Contains(clickedItemId)
                ? currentSelection.ToHashSet()
                : target;
        }

        var result = currentSelection.ToHashSet();
        if (target.All(result.Contains))
            result.ExceptWith(target);
        else
            result.UnionWith(target);
        return result;
    }

    public static IReadOnlySet<long> SelectItemsInMarquee(
        IReadOnlyList<BoardItemRecord> items,
        BoardWorldRect marquee,
        IReadOnlySet<long> baseline,
        bool additive)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(baseline);
        var normalized = Normalize(marquee);
        var hitIds = items
            .Where(item => Intersects(normalized, BoardCameraEngine.ItemBounds(item)))
            .Select(item => item.Id)
            .ToHashSet();
        var hitGroups = items
            .Where(item => hitIds.Contains(item.Id) && item.GroupId is not null)
            .Select(item => item.GroupId!.Value)
            .ToHashSet();
        hitIds.UnionWith(items.Where(item => item.GroupId is { } groupId && hitGroups.Contains(groupId)).Select(item => item.Id));
        if (!additive) return hitIds;
        hitIds.UnionWith(baseline);
        return hitIds;
    }

    public static IReadOnlyList<long> SelectNotesInMarquee(
        IReadOnlyList<BoardNoteRecord> notes,
        BoardWorldRect marquee)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var normalized = Normalize(marquee);
        return notes
            .Where(note => Intersects(normalized, BoardCameraEngine.NoteBounds(note)))
            .Select(note => note.Id)
            .Order()
            .ToArray();
    }

    public static bool Intersects(BoardWorldRect first, BoardWorldRect second)
    {
        first = Normalize(first);
        second = Normalize(second);
        return first.X <= second.X + second.Width
            && first.X + first.Width >= second.X
            && first.Y <= second.Y + second.Height
            && first.Y + first.Height >= second.Y;
    }

    public static BoardWorldRect Normalize(BoardWorldRect rectangle)
    {
        var left = Math.Min(rectangle.X, rectangle.X + rectangle.Width);
        var top = Math.Min(rectangle.Y, rectangle.Y + rectangle.Height);
        return new BoardWorldRect(
            left,
            top,
            Math.Abs(rectangle.Width),
            Math.Abs(rectangle.Height));
    }
}
