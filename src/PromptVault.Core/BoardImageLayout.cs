namespace PromptVault.Core;

public enum BoardImageLayoutKind
{
    Left, Center, Right, Top, Middle, Bottom, HorizontalSpacing, VerticalSpacing
}

public static class BoardImageLayout
{
    // Use the displayed frame's rotated bounds; the original asset and crop stay untouched.
    public static IReadOnlyList<BoardItemRecord> Arrange(
        IReadOnlyList<BoardItemRecord> selected, BoardImageLayoutKind kind)
    {
        var distribute = kind is BoardImageLayoutKind.HorizontalSpacing or BoardImageLayoutKind.VerticalSpacing;
        if (selected.Count < (distribute ? 3 : 2)) return selected;
        var entries = selected.Select(item => (Item: item, Bounds: BoardCameraEngine.ItemBounds(item))).ToArray();
        if (entries.Any(entry => !double.IsFinite(entry.Bounds.X) || !double.IsFinite(entry.Bounds.Y)
            || !double.IsFinite(entry.Bounds.Width) || !double.IsFinite(entry.Bounds.Height)))
            throw new InvalidOperationException("图片位置无效，无法排版。");
        var left = entries.Min(entry => entry.Bounds.X);
        var top = entries.Min(entry => entry.Bounds.Y);
        var right = entries.Max(entry => entry.Bounds.X + entry.Bounds.Width);
        var bottom = entries.Max(entry => entry.Bounds.Y + entry.Bounds.Height);
        if (!distribute)
            return entries.Select(entry => entry.Item with
            {
                X = entry.Item.X + (kind switch
                {
                    BoardImageLayoutKind.Left => left - entry.Bounds.X,
                    BoardImageLayoutKind.Center => (left + right - entry.Bounds.Width) / 2 - entry.Bounds.X,
                    BoardImageLayoutKind.Right => right - entry.Bounds.Width - entry.Bounds.X,
                    _ => 0
                }),
                Y = entry.Item.Y + (kind switch
                {
                    BoardImageLayoutKind.Top => top - entry.Bounds.Y,
                    BoardImageLayoutKind.Middle => (top + bottom - entry.Bounds.Height) / 2 - entry.Bounds.Y,
                    BoardImageLayoutKind.Bottom => bottom - entry.Bounds.Height - entry.Bounds.Y,
                    _ => 0
                })
            }).ToArray();

        var horizontal = kind == BoardImageLayoutKind.HorizontalSpacing;
        var ordered = entries.OrderBy(entry => horizontal ? entry.Bounds.X : entry.Bounds.Y)
            .ThenBy(entry => entry.Item.Id).ToArray();
        double Start(BoardWorldRect bounds) => horizontal ? bounds.X : bounds.Y;
        double Size(BoardWorldRect bounds) => horizontal ? bounds.Width : bounds.Height;
        var span = Start(ordered[^1].Bounds) + Size(ordered[^1].Bounds) - Start(ordered[0].Bounds);
        var gap = (span - ordered.Sum(entry => Size(entry.Bounds))) / (ordered.Length - 1);
        if (gap < -0.000001)
            throw new InvalidOperationException("两端图片之间空间不足，请先拉开距离再等间距排列。");
        gap = Math.Max(0, gap);
        var cursor = Start(ordered[0].Bounds) + Size(ordered[0].Bounds) + gap;
        var result = selected.ToDictionary(item => item.Id);
        for (var index = 1; index < ordered.Length - 1; index++)
        {
            var entry = ordered[index];
            var delta = cursor - Start(entry.Bounds);
            result[entry.Item.Id] = horizontal
                ? entry.Item with { X = entry.Item.X + delta }
                : entry.Item with { Y = entry.Item.Y + delta };
            cursor += Size(entry.Bounds) + gap;
        }
        return selected.Select(item => result[item.Id]).ToArray();
    }
}
