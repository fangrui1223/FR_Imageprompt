namespace PromptVault.Core;

public enum BoardResizeHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

public sealed record BoardResizeGesture(
    BoardWorldRect OriginalBounds,
    BoardResizeHandle Handle,
    double PointerStartX,
    double PointerStartY,
    double MinimumEdge = BoardTransformEngine.MinimumObjectEdge)
{
    public BoardWorldRect Calculate(
        double pointerX,
        double pointerY,
        bool preserveAspect,
        bool fromCenter) =>
        BoardTransformEngine.ResizeBounds(
            OriginalBounds,
            Handle,
            pointerX - PointerStartX,
            pointerY - PointerStartY,
            preserveAspect,
            fromCenter,
            MinimumEdge);
}

public static class BoardTransformEngine
{
    public const double MinimumObjectEdge = 24;

    public static (double X, double Y) ScreenDeltaToLocal(
        double screenDeltaX,
        double screenDeltaY,
        double zoom,
        double rotationDegrees)
    {
        zoom = BoardViewportEngine.ClampZoom(zoom);
        var worldX = (double.IsFinite(screenDeltaX) ? screenDeltaX : 0) / zoom;
        var worldY = (double.IsFinite(screenDeltaY) ? screenDeltaY : 0) / zoom;
        var rotation = rotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(rotation);
        var sine = Math.Sin(rotation);
        return (
            worldX * cosine + worldY * sine,
            -worldX * sine + worldY * cosine);
    }

    public static BoardItemRecord ResetSize(
        BoardItemRecord item,
        double targetLongEdge = 320,
        double minimumShortEdge = 48)
    {
        targetLongEdge = Math.Max(MinimumObjectEdge, targetLongEdge);
        minimumShortEdge = Math.Clamp(minimumShortEdge, MinimumObjectEdge, targetLongEdge);
        var fallbackAspect = item.Width / Math.Max(0.000001, item.Height);
        var naturalAspect = item.NaturalWidth > 0 && item.NaturalHeight > 0
            ? item.NaturalWidth / (double)item.NaturalHeight
            : fallbackAspect;
        var aspect = Math.Clamp(
            double.IsFinite(naturalAspect) && naturalAspect > 0 ? naturalAspect : 1,
            0.1,
            10);
        var width = aspect >= 1 ? targetLongEdge : targetLongEdge * aspect;
        var height = aspect >= 1 ? targetLongEdge / aspect : targetLongEdge;
        if (Math.Min(width, height) < minimumShortEdge)
        {
            var scale = minimumShortEdge / Math.Max(0.000001, Math.Min(width, height));
            width *= scale;
            height *= scale;
        }
        var centerX = item.X + item.Width / 2;
        var centerY = item.Y + item.Height / 2;
        return item with
        {
            X = centerX - width / 2,
            Y = centerY - height / 2,
            Width = width,
            Height = height
        };
    }

    public static BoardWorldRect ResizeBounds(
        BoardWorldRect original,
        BoardResizeHandle handle,
        double deltaX,
        double deltaY,
        bool preserveAspect,
        bool fromCenter,
        double minimumEdge = MinimumObjectEdge,
        double? minimumHeight = null)
    {
        original = BoardInteractionEngine.Normalize(original);
        var minimumWidth = Math.Max(0.000001, minimumEdge);
        var resolvedMinimumHeight = Math.Max(0.000001, minimumHeight ?? minimumEdge);
        var sx = handle is BoardResizeHandle.TopLeft or BoardResizeHandle.BottomLeft or BoardResizeHandle.Left ? -1d
            : handle is BoardResizeHandle.TopRight or BoardResizeHandle.BottomRight or BoardResizeHandle.Right ? 1d
            : 0d;
        var sy = handle is BoardResizeHandle.TopLeft or BoardResizeHandle.TopRight or BoardResizeHandle.Top ? -1d
            : handle is BoardResizeHandle.BottomLeft or BoardResizeHandle.BottomRight or BoardResizeHandle.Bottom ? 1d
            : 0d;
        var width = sx == 0
            ? original.Width
            : Math.Max(minimumWidth, original.Width + sx * deltaX * (fromCenter ? 2 : 1));
        var height = sy == 0
            ? original.Height
            : Math.Max(resolvedMinimumHeight, original.Height + sy * deltaY * (fromCenter ? 2 : 1));
        if (preserveAspect && sx != 0 && sy != 0)
        {
            // Project the pointer displacement onto the original corner diagonal. The old
            // dominant-axis choice changed branches when width/height deltas crossed and made
            // the same continuous drag jump between two different sizes.
            var anchorFactor = fromCenter ? 0.5 : 1d;
            var diagonalX = sx * original.Width * anchorFactor;
            var diagonalY = sy * original.Height * anchorFactor;
            var diagonalLengthSquared = diagonalX * diagonalX + diagonalY * diagonalY;
            var safeDeltaX = double.IsFinite(deltaX) ? deltaX : 0;
            var safeDeltaY = double.IsFinite(deltaY) ? deltaY : 0;
            var scale = diagonalLengthSquared <= 0.000001
                ? 1d
                : 1d + (safeDeltaX * diagonalX + safeDeltaY * diagonalY) / diagonalLengthSquared;
            var minimumScale = Math.Max(
                minimumWidth / Math.Max(0.000001, original.Width),
                resolvedMinimumHeight / Math.Max(0.000001, original.Height));
            scale = Math.Max(minimumScale, double.IsFinite(scale) ? scale : 1d);
            width = Math.Max(minimumWidth, original.Width * scale);
            height = Math.Max(resolvedMinimumHeight, original.Height * scale);
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
        return originals.Select(item => selectedIds.Contains(item.Id)
            ? ScaleItem(item, originalBounds, targetBounds)
            : item).ToArray();
    }

    public static BoardItemRecord ScaleItem(
        BoardItemRecord item,
        BoardWorldRect originalBounds,
        BoardWorldRect targetBounds)
    {
        originalBounds = BoardInteractionEngine.Normalize(originalBounds);
        targetBounds = BoardInteractionEngine.Normalize(targetBounds);
        var scaleX = targetBounds.Width / Math.Max(0.000001, originalBounds.Width);
        var scaleY = targetBounds.Height / Math.Max(0.000001, originalBounds.Height);
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
        if (handle is BoardResizeHandle.TopLeft or BoardResizeHandle.BottomLeft or BoardResizeHandle.Left) left += dx;
        else if (handle is BoardResizeHandle.TopRight or BoardResizeHandle.BottomRight or BoardResizeHandle.Right) right -= dx;
        if (handle is BoardResizeHandle.TopLeft or BoardResizeHandle.TopRight or BoardResizeHandle.Top) top += dy;
        else if (handle is BoardResizeHandle.BottomLeft or BoardResizeHandle.BottomRight or BoardResizeHandle.Bottom) bottom -= dy;
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
