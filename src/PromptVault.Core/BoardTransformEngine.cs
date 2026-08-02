namespace PromptVault.Core;

public enum BoardResizeHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public static class BoardTransformEngine
{
    public const double MinimumObjectEdge = 24;

    public static BoardWorldRect ResizeBounds(
        BoardWorldRect original,
        BoardResizeHandle handle,
        double deltaX,
        double deltaY,
        bool preserveAspect,
        bool fromCenter,
        double minimumEdge = MinimumObjectEdge)
    {
        original = BoardInteractionEngine.Normalize(original);
        var sx = handle is BoardResizeHandle.TopLeft or BoardResizeHandle.BottomLeft ? -1d : 1d;
        var sy = handle is BoardResizeHandle.TopLeft or BoardResizeHandle.TopRight ? -1d : 1d;
        var width = Math.Max(minimumEdge, original.Width + sx * deltaX * (fromCenter ? 2 : 1));
        var height = Math.Max(minimumEdge, original.Height + sy * deltaY * (fromCenter ? 2 : 1));
        if (preserveAspect)
        {
            var widthScale = width / Math.Max(0.000001, original.Width);
            var heightScale = height / Math.Max(0.000001, original.Height);
            var scale = Math.Abs(widthScale - 1) >= Math.Abs(heightScale - 1) ? widthScale : heightScale;
            var minimumScale = Math.Max(
                minimumEdge / Math.Max(0.000001, original.Width),
                minimumEdge / Math.Max(0.000001, original.Height));
            scale = Math.Max(minimumScale, scale);
            width = Math.Max(minimumEdge, original.Width * scale);
            height = Math.Max(minimumEdge, original.Height * scale);
        }

        if (fromCenter)
        {
            var centerX = original.X + original.Width / 2;
            var centerY = original.Y + original.Height / 2;
            return new BoardWorldRect(centerX - width / 2, centerY - height / 2, width, height);
        }

        var x = sx < 0 ? original.X + original.Width - width : original.X;
        var y = sy < 0 ? original.Y + original.Height - height : original.Y;
        return new BoardWorldRect(x, y, width, height);
    }

    public static IReadOnlyList<BoardItemRecord> ScaleSelection(
        IReadOnlyList<BoardItemRecord> originals,
        IReadOnlySet<long> selectedIds,
        BoardWorldRect originalBounds,
        BoardWorldRect targetBounds)
    {
        ArgumentNullException.ThrowIfNull(originals);
        ArgumentNullException.ThrowIfNull(selectedIds);
        originalBounds = BoardInteractionEngine.Normalize(originalBounds);
        targetBounds = BoardInteractionEngine.Normalize(targetBounds);
        var scaleX = targetBounds.Width / Math.Max(0.000001, originalBounds.Width);
        var scaleY = targetBounds.Height / Math.Max(0.000001, originalBounds.Height);
        return originals.Select(item =>
        {
            if (!selectedIds.Contains(item.Id)) return item;
            var centerX = item.X + item.Width / 2;
            var centerY = item.Y + item.Height / 2;
            var targetCenterX = targetBounds.X + (centerX - originalBounds.X) * scaleX;
            var targetCenterY = targetBounds.Y + (centerY - originalBounds.Y) * scaleY;
            var width = Math.Max(MinimumObjectEdge, item.Width * scaleX);
            var height = Math.Max(MinimumObjectEdge, item.Height * scaleY);
            return item with
            {
                X = targetCenterX - width / 2,
                Y = targetCenterY - height / 2,
                Width = width,
                Height = height
            };
        }).ToArray();
    }

    public static IReadOnlyList<BoardItemRecord> RotateSelection(
        IReadOnlyList<BoardItemRecord> originals,
        IReadOnlySet<long> selectedIds,
        double centerX,
        double centerY,
        double deltaDegrees,
        bool snapToFifteenDegrees)
    {
        ArgumentNullException.ThrowIfNull(originals);
        ArgumentNullException.ThrowIfNull(selectedIds);
        if (snapToFifteenDegrees) deltaDegrees = Math.Round(deltaDegrees / 15) * 15;
        var radians = deltaDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return originals.Select(item =>
        {
            if (!selectedIds.Contains(item.Id)) return item;
            var itemCenterX = item.X + item.Width / 2;
            var itemCenterY = item.Y + item.Height / 2;
            var dx = itemCenterX - centerX;
            var dy = itemCenterY - centerY;
            var rotatedX = centerX + dx * cosine - dy * sine;
            var rotatedY = centerY + dx * sine + dy * cosine;
            return item with
            {
                X = rotatedX - item.Width / 2,
                Y = rotatedY - item.Height / 2,
                Rotation = NormalizeDegrees(item.Rotation + deltaDegrees)
            };
        }).ToArray();
    }

    public static BoardItemRecord CropFromCorner(
        BoardItemRecord item,
        BoardResizeHandle handle,
        double deltaX,
        double deltaY)
    {
        var left = item.CropLeft;
        var top = item.CropTop;
        var right = item.CropRight;
        var bottom = item.CropBottom;
        var dx = deltaX / Math.Max(1, item.Width);
        var dy = deltaY / Math.Max(1, item.Height);
        if (handle is BoardResizeHandle.TopLeft or BoardResizeHandle.BottomLeft) left += dx;
        else right -= dx;
        if (handle is BoardResizeHandle.TopLeft or BoardResizeHandle.TopRight) top += dy;
        else bottom -= dy;
        left = Math.Clamp(left, 0, Math.Max(0, 0.95 - right));
        right = Math.Clamp(right, 0, Math.Max(0, 0.95 - left));
        top = Math.Clamp(top, 0, Math.Max(0, 0.95 - bottom));
        bottom = Math.Clamp(bottom, 0, Math.Max(0, 0.95 - top));
        return item with { CropLeft = left, CropTop = top, CropRight = right, CropBottom = bottom };
    }

    public static double AngleDegrees(double centerX, double centerY, double x, double y) =>
        Math.Atan2(y - centerY, x - centerX) * 180 / Math.PI;

    public static double NormalizeDegrees(double value)
    {
        value %= 360;
        if (value > 180) value -= 360;
        if (value <= -180) value += 360;
        return value;
    }
}
