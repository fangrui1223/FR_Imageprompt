using System.Runtime.CompilerServices;

namespace PromptVault.Core;

public static class BoardViewportEngine
{
    private sealed record VisibilityBounds(BoardWorldRect[] Items, IReadOnlyDictionary<long, int> IdIndexes);
    private static readonly ConditionalWeakTable<IReadOnlyList<BoardItemRecord>, VisibilityBounds> BoundsCache = new();
    public const double MinimumZoom = 0.1;
    public const double MaximumZoom = 8;

    public static BoardViewport Pan(BoardViewport viewport, double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY)) return viewport;
        return viewport with
        {
            OffsetX = viewport.OffsetX + deltaX,
            OffsetY = viewport.OffsetY + deltaY
        };
    }

    public static BoardViewport ZoomAt(
        BoardViewport viewport,
        double screenX,
        double screenY,
        double requestedZoom)
    {
        var oldZoom = ClampZoom(viewport.Zoom);
        var newZoom = ClampZoom(requestedZoom);
        var worldX = (screenX - viewport.OffsetX) / oldZoom;
        var worldY = (screenY - viewport.OffsetY) / oldZoom;
        return viewport with
        {
            Zoom = newZoom,
            OffsetX = screenX - worldX * newZoom,
            OffsetY = screenY - worldY * newZoom
        };
    }

    public static BoardWorldRect VisibleWorldRect(BoardViewport viewport, double overscanPixels = 240)
    {
        var zoom = ClampZoom(viewport.Zoom);
        var overscan = Math.Max(0, overscanPixels);
        return new BoardWorldRect(
            (-viewport.OffsetX - overscan) / zoom,
            (-viewport.OffsetY - overscan) / zoom,
            (Math.Max(0, viewport.Width) + overscan * 2) / zoom,
            (Math.Max(0, viewport.Height) + overscan * 2) / zoom);
    }

    public static IReadOnlyList<BoardItemRecord> QueryVisible(
        IReadOnlyList<BoardItemRecord> items,
        BoardViewport viewport,
        double overscanPixels = 240)
    {
        var visible = VisibleWorldRect(viewport, overscanPixels);
        var result = new List<BoardItemRecord>(Math.Min(items.Count, 256));
        if (!BoundsCache.TryGetValue(items, out var cached))
        {
            cached = new VisibilityBounds(
                items.Select(BoardCameraEngine.ItemBounds).ToArray(),
                items.Select((item, index) => (item.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index));
            BoundsCache.Add(items, cached);
        }
        var boundsItems = cached.Items;
        for (var index = 0; index < items.Count; index++)
        {
            var bounds = boundsItems[index];
            if (Intersects(visible, bounds.X, bounds.Y, bounds.Width, bounds.Height))
                result.Add(items[index]);
        }
        return result;
    }

    public static void Invalidate(IReadOnlyList<BoardItemRecord> items) => BoundsCache.Remove(items);

    public static void RefreshBounds(
        IReadOnlyList<BoardItemRecord> items,
        IEnumerable<long> changedItemIds)
    {
        if (!BoundsCache.TryGetValue(items, out var cached)) return;
        foreach (var id in changedItemIds.Distinct())
        {
            if (!cached.IdIndexes.TryGetValue(id, out var index) || index >= items.Count) continue;
            cached.Items[index] = BoardCameraEngine.ItemBounds(items[index]);
        }
    }

    public static IReadOnlyList<BoardNoteRecord> QueryVisible(
        IReadOnlyList<BoardNoteRecord> notes,
        BoardViewport viewport,
        double overscanPixels = 240)
    {
        var visible = VisibleWorldRect(viewport, overscanPixels);
        var result = new List<BoardNoteRecord>(Math.Min(notes.Count, 64));
        foreach (var note in notes)
        {
            if (Intersects(visible, note.X, note.Y, note.Width, note.Height))
                result.Add(note);
        }
        return result;
    }

    public static (double X, double Y) ScreenToWorld(
        BoardViewport viewport,
        double screenX,
        double screenY)
    {
        var zoom = ClampZoom(viewport.Zoom);
        return ((screenX - viewport.OffsetX) / zoom, (screenY - viewport.OffsetY) / zoom);
    }

    public static double ClampZoom(double zoom) =>
        Math.Clamp(double.IsFinite(zoom) ? zoom : 1, MinimumZoom, MaximumZoom);

    private static bool Intersects(
        BoardWorldRect viewport,
        double x,
        double y,
        double width,
        double height) =>
        x + width >= viewport.X
        && y + height >= viewport.Y
        && x <= viewport.X + viewport.Width
        && y <= viewport.Y + viewport.Height;
}
