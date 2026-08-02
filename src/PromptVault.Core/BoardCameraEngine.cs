namespace PromptVault.Core;

public readonly record struct BoardBoundsResult(bool HasValue, BoardWorldRect Bounds)
{
    public static BoardBoundsResult Empty => new(false, default);

    public static BoardBoundsResult From(BoardWorldRect bounds) => new(true, bounds);
}

public static class BoardCameraEngine
{
    public const double MinimumFocusPadding = 32;
    public const double MaximumFocusPadding = 96;
    public const double FocusPaddingRatio = 0.06;

    public static BoardWorldRect RotatedBounds(
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees)
    {
        if (!double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || !double.IsFinite(rotationDegrees))
        {
            return new BoardWorldRect(double.NaN, double.NaN, double.NaN, double.NaN);
        }

        width = Math.Abs(width);
        height = Math.Abs(height);
        var centerX = x + width / 2;
        var centerY = y + height / 2;
        var radians = rotationDegrees * Math.PI / 180;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var rotatedWidth = width * cosine + height * sine;
        var rotatedHeight = width * sine + height * cosine;
        return new BoardWorldRect(
            centerX - rotatedWidth / 2,
            centerY - rotatedHeight / 2,
            rotatedWidth,
            rotatedHeight);
    }

    public static BoardWorldRect ItemBounds(BoardItemRecord item) =>
        RotatedBounds(item.X, item.Y, item.Width, item.Height, item.Rotation);

    public static BoardWorldRect NoteBounds(BoardNoteRecord note) =>
        new(note.X, note.Y, Math.Max(0, note.Width), Math.Max(0, note.Height));

    public static BoardBoundsResult ContentBounds(
        IEnumerable<BoardItemRecord> items,
        IEnumerable<BoardNoteRecord> notes)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(notes);
        return Union(items.Select(ItemBounds).Concat(notes.Select(NoteBounds)));
    }

    public static BoardBoundsResult SelectionBounds(
        IEnumerable<BoardItemRecord> items,
        IReadOnlySet<long> selectedItemIds,
        IEnumerable<BoardNoteRecord> notes,
        long? selectedNoteId)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(selectedItemIds);
        ArgumentNullException.ThrowIfNull(notes);
        var rectangles = items
            .Where(item => selectedItemIds.Contains(item.Id))
            .Select(ItemBounds);
        if (selectedNoteId is { } noteId)
        {
            rectangles = rectangles.Concat(
                notes.Where(note => note.Id == noteId).Select(NoteBounds));
        }
        return Union(rectangles);
    }

    public static BoardBoundsResult Union(IEnumerable<BoardWorldRect> rectangles)
    {
        ArgumentNullException.ThrowIfNull(rectangles);
        var hasBounds = false;
        var minX = 0d;
        var minY = 0d;
        var maxX = 0d;
        var maxY = 0d;
        foreach (var rectangle in rectangles)
        {
            if (!IsFinite(rectangle)) continue;
            var left = Math.Min(rectangle.X, rectangle.X + rectangle.Width);
            var top = Math.Min(rectangle.Y, rectangle.Y + rectangle.Height);
            var right = Math.Max(rectangle.X, rectangle.X + rectangle.Width);
            var bottom = Math.Max(rectangle.Y, rectangle.Y + rectangle.Height);
            if (!hasBounds)
            {
                minX = left;
                minY = top;
                maxX = right;
                maxY = bottom;
                hasBounds = true;
                continue;
            }
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, right);
            maxY = Math.Max(maxY, bottom);
        }
        return hasBounds
            ? BoardBoundsResult.From(new BoardWorldRect(minX, minY, maxX - minX, maxY - minY))
            : BoardBoundsResult.Empty;
    }

    public static BoardViewport FitBounds(
        BoardBoundsResult bounds,
        double viewportWidth,
        double viewportHeight)
    {
        viewportWidth = SanitizeViewportLength(viewportWidth);
        viewportHeight = SanitizeViewportLength(viewportHeight);
        if (!bounds.HasValue || !IsFinite(bounds.Bounds))
        {
            return new BoardViewport(
                viewportWidth / 2,
                viewportHeight / 2,
                1,
                viewportWidth,
                viewportHeight);
        }

        var shortEdge = Math.Min(viewportWidth, viewportHeight);
        var padding = Math.Clamp(
            shortEdge * FocusPaddingRatio,
            MinimumFocusPadding,
            MaximumFocusPadding);
        var availableWidth = Math.Max(1, viewportWidth - padding * 2);
        var availableHeight = Math.Max(1, viewportHeight - padding * 2);
        var width = Math.Max(0.000001, Math.Abs(bounds.Bounds.Width));
        var height = Math.Max(0.000001, Math.Abs(bounds.Bounds.Height));
        var zoom = BoardViewportEngine.ClampZoom(
            Math.Min(availableWidth / width, availableHeight / height));
        var centerX = bounds.Bounds.X + bounds.Bounds.Width / 2;
        var centerY = bounds.Bounds.Y + bounds.Bounds.Height / 2;
        return new BoardViewport(
            viewportWidth / 2 - centerX * zoom,
            viewportHeight / 2 - centerY * zoom,
            zoom,
            viewportWidth,
            viewportHeight);
    }

    public static BoardViewport Interpolate(
        BoardViewport start,
        BoardViewport end,
        double progress)
    {
        progress = Math.Clamp(double.IsFinite(progress) ? progress : 1, 0, 1);
        return new BoardViewport(
            Lerp(start.OffsetX, end.OffsetX, progress),
            Lerp(start.OffsetY, end.OffsetY, progress),
            Lerp(start.Zoom, end.Zoom, progress),
            end.Width,
            end.Height);
    }

    public static long? AdjacentItemId(
        IEnumerable<BoardItemRecord> items,
        long currentItemId,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .ToArray();
        if (ordered.Length == 0 || direction == 0) return null;
        var currentIndex = Array.IndexOf(ordered, currentItemId);
        if (currentIndex < 0) return direction > 0 ? ordered[0] : ordered[^1];
        var next = (currentIndex + Math.Sign(direction) + ordered.Length) % ordered.Length;
        return ordered[next];
    }

    private static bool IsFinite(BoardWorldRect rectangle) =>
        double.IsFinite(rectangle.X)
        && double.IsFinite(rectangle.Y)
        && double.IsFinite(rectangle.Width)
        && double.IsFinite(rectangle.Height);

    private static double SanitizeViewportLength(double length) =>
        double.IsFinite(length) && length > 0 ? length : 1;

    private static double Lerp(double start, double end, double progress) =>
        start + (end - start) * progress;
}

public enum BoardFocusTargetKind
{
    FullBoard,
    Selection,
    SingleItem,
    SingleNote
}

public sealed class BoardFocusTarget : IEquatable<BoardFocusTarget>
{
    private readonly long[] _itemIds;

    private BoardFocusTarget(
        BoardFocusTargetKind kind,
        IEnumerable<long> itemIds,
        long? noteId)
    {
        Kind = kind;
        _itemIds = itemIds.Distinct().Order().ToArray();
        NoteId = noteId;
    }

    public BoardFocusTargetKind Kind { get; }

    public IReadOnlyList<long> ItemIds => _itemIds;

    public long? NoteId { get; }

    public long? SingleItemId =>
        Kind == BoardFocusTargetKind.SingleItem && _itemIds.Length == 1
            ? _itemIds[0]
            : null;

    public static BoardFocusTarget FullBoard() =>
        new(BoardFocusTargetKind.FullBoard, [], null);

    public static BoardFocusTarget Selection(
        IEnumerable<long> itemIds,
        long? noteId = null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.ToArray();
        if (ids.Length == 1 && noteId is null) return SingleItem(ids[0]);
        if (ids.Length == 0 && noteId is { } id) return SingleNote(id);
        return new BoardFocusTarget(BoardFocusTargetKind.Selection, ids, noteId);
    }

    public static BoardFocusTarget SingleItem(long itemId) =>
        new(BoardFocusTargetKind.SingleItem, [itemId], null);

    public static BoardFocusTarget SingleNote(long noteId) =>
        new(BoardFocusTargetKind.SingleNote, [], noteId);

    public bool Equals(BoardFocusTarget? other) =>
        other is not null
        && Kind == other.Kind
        && NoteId == other.NoteId
        && _itemIds.SequenceEqual(other._itemIds);

    public override bool Equals(object? obj) => obj is BoardFocusTarget other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(NoteId);
        foreach (var itemId in _itemIds) hash.Add(itemId);
        return hash.ToHashCode();
    }
}

public readonly record struct BoardCameraSnapshot(
    double OffsetX,
    double OffsetY,
    double Zoom,
    double ViewportWidth,
    double ViewportHeight)
{
    public static BoardCameraSnapshot Capture(BoardViewport viewport) => new(
        viewport.OffsetX,
        viewport.OffsetY,
        BoardViewportEngine.ClampZoom(viewport.Zoom),
        Math.Max(1, viewport.Width),
        Math.Max(1, viewport.Height));

    public BoardViewport Restore(double viewportWidth, double viewportHeight)
    {
        viewportWidth = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1;
        viewportHeight = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 1;
        var zoom = BoardViewportEngine.ClampZoom(Zoom);
        var worldCenterX = (ViewportWidth / 2 - OffsetX) / zoom;
        var worldCenterY = (ViewportHeight / 2 - OffsetY) / zoom;
        return new BoardViewport(
            viewportWidth / 2 - worldCenterX * zoom,
            viewportHeight / 2 - worldCenterY * zoom,
            zoom,
            viewportWidth,
            viewportHeight);
    }
}

public readonly record struct BoardCameraTransition(
    BoardViewport Start,
    BoardViewport End,
    BoardFocusTarget Target,
    long Generation,
    bool IsRestore);

public sealed class BoardFocusController
{
    private BoardFocusSession? _active;
    private BoardCameraSnapshot? _pendingRestore;
    private BoardCameraTransition? _transition;
    private long _generation;

    public bool IsActive => _active is not null;

    public BoardFocusTarget? Target => _active?.Target;

    public BoardCameraTransition? Transition => _transition;

    public long Generation => _generation;

    public BoardCameraTransition ToggleSpace(
        BoardViewport current,
        BoardFocusTarget target,
        BoardBoundsResult bounds) =>
        _active is null
            ? Focus(current, target, bounds)
            : Restore(current);

    public BoardCameraTransition FocusOrRestoreSameTarget(
        BoardViewport current,
        BoardFocusTarget target,
        BoardBoundsResult bounds) =>
        _active?.Target.Equals(target) == true
            ? Restore(current)
            : Focus(current, target, bounds);

    public BoardCameraTransition ForceFocus(
        BoardViewport current,
        BoardFocusTarget target,
        BoardBoundsResult bounds) =>
        Focus(current, target, bounds);

    public BoardCameraTransition Refit(
        BoardViewport current,
        BoardBoundsResult bounds)
    {
        if (_active is not { } active)
            throw new InvalidOperationException("没有活动的画板聚焦会话。");
        var end = BoardCameraEngine.FitBounds(bounds, current.Width, current.Height);
        return SetTransition(current, end, active.Target, isRestore: false);
    }

    public BoardCameraTransition ResizePendingRestore(BoardViewport current)
    {
        if (_pendingRestore is not { } snapshot)
            throw new InvalidOperationException("没有等待恢复的画板相机。");
        var end = snapshot.Restore(current.Width, current.Height);
        var target = _transition?.Target ?? BoardFocusTarget.FullBoard();
        return SetTransition(current, end, target, isRestore: true);
    }

    public BoardViewport WorkingViewport(BoardViewport current)
    {
        var snapshot = _active?.WorkingCamera ?? _pendingRestore;
        return snapshot?.Restore(current.Width, current.Height) ?? current;
    }

    public void CompleteTransition(long generation)
    {
        if (_transition is not { } transition || transition.Generation != generation) return;
        if (transition.IsRestore) _pendingRestore = null;
        _transition = null;
    }

    public void InterruptTransition()
    {
        if (_transition?.IsRestore == true) _pendingRestore = null;
        _transition = null;
        _generation++;
    }

    public void Reset()
    {
        _active = null;
        _pendingRestore = null;
        _transition = null;
        _generation++;
    }

    private BoardCameraTransition Focus(
        BoardViewport current,
        BoardFocusTarget target,
        BoardBoundsResult bounds)
    {
        ArgumentNullException.ThrowIfNull(target);
        var workingCamera = _active?.WorkingCamera
            ?? _pendingRestore
            ?? BoardCameraSnapshot.Capture(current);
        _pendingRestore = null;
        _active = new BoardFocusSession(workingCamera, target);
        var end = BoardCameraEngine.FitBounds(bounds, current.Width, current.Height);
        return SetTransition(current, end, target, isRestore: false);
    }

    private BoardCameraTransition Restore(BoardViewport current)
    {
        if (_active is not { } active)
            throw new InvalidOperationException("没有活动的画板聚焦会话。");
        var end = active.WorkingCamera.Restore(current.Width, current.Height);
        _pendingRestore = active.WorkingCamera;
        _active = null;
        return SetTransition(current, end, active.Target, isRestore: true);
    }

    private BoardCameraTransition SetTransition(
        BoardViewport start,
        BoardViewport end,
        BoardFocusTarget target,
        bool isRestore)
    {
        _transition = new BoardCameraTransition(
            start,
            end,
            target,
            ++_generation,
            isRestore);
        return _transition.Value;
    }

    private sealed record BoardFocusSession(
        BoardCameraSnapshot WorkingCamera,
        BoardFocusTarget Target);
}
