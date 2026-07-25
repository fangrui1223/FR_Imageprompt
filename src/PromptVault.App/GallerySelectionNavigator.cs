namespace PromptVault.App;

public enum GalleryNavigationDirection
{
    Left,
    Right,
    Up,
    Down,
    Home,
    End
}

public sealed record GalleryCardPosition(
    long Id,
    double X,
    double Y,
    double Width,
    double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

public static class GallerySelectionNavigator
{
    public static IReadOnlyList<long> Range(
        IReadOnlyList<long> orderedIds,
        long anchorId,
        long targetId)
    {
        var anchorIndex = IndexOf(orderedIds, anchorId);
        var targetIndex = IndexOf(orderedIds, targetId);
        if (anchorIndex < 0 || targetIndex < 0) return [targetId];
        var start = Math.Min(anchorIndex, targetIndex);
        var count = Math.Abs(anchorIndex - targetIndex) + 1;
        return orderedIds.Skip(start).Take(count).ToArray();
    }

    public static long? Move(
        IReadOnlyList<GalleryCardPosition> positions,
        long? currentId,
        GalleryNavigationDirection direction)
    {
        if (positions.Count == 0) return null;
        if (currentId is null) return positions[0].Id;
        if (direction == GalleryNavigationDirection.Home) return positions[0].Id;
        if (direction == GalleryNavigationDirection.End) return positions[^1].Id;

        var current = positions.FirstOrDefault(position => position.Id == currentId.Value);
        if (current is null) return positions[0].Id;
        IEnumerable<GalleryCardPosition> candidates = direction switch
        {
            GalleryNavigationDirection.Left =>
                positions.Where(candidate => candidate.CenterX < current.CenterX - 0.1),
            GalleryNavigationDirection.Right =>
                positions.Where(candidate => candidate.CenterX > current.CenterX + 0.1),
            GalleryNavigationDirection.Up =>
                positions.Where(candidate => candidate.CenterY < current.CenterY - 0.1),
            GalleryNavigationDirection.Down =>
                positions.Where(candidate => candidate.CenterY > current.CenterY + 0.1),
            _ => []
        };

        var target = direction is GalleryNavigationDirection.Left or GalleryNavigationDirection.Right
            ? candidates
                .OrderBy(candidate =>
                    Math.Abs(candidate.CenterY - current.CenterY) * 4
                    + Math.Abs(candidate.CenterX - current.CenterX))
                .ThenBy(candidate => Math.Abs(candidate.CenterX - current.CenterX))
                .FirstOrDefault()
            : candidates
                .OrderBy(candidate =>
                    Math.Abs(candidate.CenterX - current.CenterX) * 4
                    + Math.Abs(candidate.CenterY - current.CenterY))
                .ThenBy(candidate => Math.Abs(candidate.CenterY - current.CenterY))
                .FirstOrDefault();
        return target?.Id ?? current.Id;
    }

    public static long? Move(
        IReadOnlyList<IReadOnlyList<long>> rows,
        long? currentId,
        GalleryNavigationDirection direction)
    {
        if (rows.Count == 0) return null;
        var flattened = rows.SelectMany(row => row).ToArray();
        if (flattened.Length == 0) return null;
        if (currentId is null) return flattened[0];

        var flatIndex = Array.IndexOf(flattened, currentId.Value);
        if (direction == GalleryNavigationDirection.Home) return flattened[0];
        if (direction == GalleryNavigationDirection.End) return flattened[^1];
        if (direction == GalleryNavigationDirection.Left)
            return flattened[Math.Max(0, flatIndex < 0 ? 0 : flatIndex - 1)];
        if (direction == GalleryNavigationDirection.Right)
            return flattened[Math.Min(flattened.Length - 1, flatIndex < 0 ? 0 : flatIndex + 1)];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var columnIndex = IndexOf(rows[rowIndex], currentId.Value);
            if (columnIndex < 0) continue;
            var targetRowIndex = direction == GalleryNavigationDirection.Up
                ? Math.Max(0, rowIndex - 1)
                : Math.Min(rows.Count - 1, rowIndex + 1);
            var targetRow = rows[targetRowIndex];
            if (targetRow.Count == 0) return currentId;
            return targetRow[Math.Min(columnIndex, targetRow.Count - 1)];
        }

        return flattened[0];
    }

    private static int IndexOf(IReadOnlyList<long> values, long value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value) return index;
        }
        return -1;
    }
}
