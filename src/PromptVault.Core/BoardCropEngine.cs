namespace PromptVault.Core;

public readonly record struct BoardCropViewport(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && X >= 0 && Y >= 0 && Width > 0 && Height > 0
        && Right <= 1.000001 && Bottom <= 1.000001;
}

public static class BoardCropEngine
{
    public const double MaximumZoom = 8;
    public const double WheelStep = 1.12;

    public static BoardCropViewport MinimumCover(
        double naturalWidth,
        double naturalHeight,
        double frameWidth,
        double frameHeight)
    {
        var sourceAspect = SafeAspect(naturalWidth, naturalHeight);
        var frameAspect = SafeAspect(frameWidth, frameHeight);
        if (sourceAspect > frameAspect)
        {
            var width = Math.Clamp(frameAspect / sourceAspect, 0.000001, 1);
            return new BoardCropViewport((1 - width) / 2, 0, width, 1);
        }
        var height = Math.Clamp(sourceAspect / frameAspect, 0.000001, 1);
        return new BoardCropViewport(0, (1 - height) / 2, 1, height);
    }

    public static BoardCropViewport FromItem(BoardItemRecord item)
    {
        var stored = new BoardCropViewport(
            item.CropLeft,
            item.CropTop,
            1 - item.CropLeft - item.CropRight,
            1 - item.CropTop - item.CropBottom);
        if (!stored.IsValid) stored = new BoardCropViewport(0, 0, 1, 1);
        return EffectiveCover(stored, item.NaturalWidth, item.NaturalHeight, item.Width, item.Height);
    }

    public static BoardCropViewport EffectiveCover(
        BoardCropViewport source,
        double naturalWidth,
        double naturalHeight,
        double frameWidth,
        double frameHeight)
    {
        if (!source.IsValid) source = new BoardCropViewport(0, 0, 1, 1);
        var sourceAspect = SafeAspect(naturalWidth * source.Width, naturalHeight * source.Height);
        var frameAspect = SafeAspect(frameWidth, frameHeight);
        if (sourceAspect > frameAspect)
        {
            var width = source.Width * frameAspect / sourceAspect;
            return new BoardCropViewport(source.X + (source.Width - width) / 2, source.Y, width, source.Height);
        }
        var height = source.Height * sourceAspect / frameAspect;
        return new BoardCropViewport(source.X, source.Y + (source.Height - height) / 2, source.Width, height);
    }

    public static BoardCropViewport ZoomAt(
        BoardCropViewport current,
        BoardCropViewport minimumCover,
        double anchorX,
        double anchorY,
        double factor)
    {
        if (!current.IsValid) current = minimumCover;
        if (!minimumCover.IsValid) throw new ArgumentException("Minimum cover must be valid.", nameof(minimumCover));
        anchorX = Math.Clamp(double.IsFinite(anchorX) ? anchorX : 0.5, 0, 1);
        anchorY = Math.Clamp(double.IsFinite(anchorY) ? anchorY : 0.5, 0, 1);
        factor = double.IsFinite(factor) && factor > 0 ? factor : 1;
        var minWidth = minimumCover.Width / MaximumZoom;
        var minHeight = minimumCover.Height / MaximumZoom;
        var width = Math.Clamp(current.Width / factor, minWidth, minimumCover.Width);
        var height = Math.Clamp(current.Height / factor, minHeight, minimumCover.Height);
        var sourceAnchorX = current.X + current.Width * anchorX;
        var sourceAnchorY = current.Y + current.Height * anchorY;
        return Clamp(new BoardCropViewport(
            sourceAnchorX - width * anchorX,
            sourceAnchorY - height * anchorY,
            width,
            height));
    }

    public static BoardCropViewport Pan(
        BoardCropViewport current,
        double frameDeltaX,
        double frameDeltaY,
        double frameWidth,
        double frameHeight)
    {
        if (!current.IsValid) throw new ArgumentException("Viewport must be valid.", nameof(current));
        var dx = double.IsFinite(frameDeltaX) ? frameDeltaX : 0;
        var dy = double.IsFinite(frameDeltaY) ? frameDeltaY : 0;
        return Clamp(current with
        {
            X = current.X - dx / Math.Max(1, frameWidth) * current.Width,
            Y = current.Y - dy / Math.Max(1, frameHeight) * current.Height
        });
    }

    public static BoardItemRecord Apply(BoardItemRecord item, BoardCropViewport viewport)
    {
        viewport = Clamp(viewport);
        return item with
        {
            CropLeft = viewport.X,
            CropTop = viewport.Y,
            CropRight = Math.Max(0, 1 - viewport.Right),
            CropBottom = Math.Max(0, 1 - viewport.Bottom)
        };
    }

    public static BoardCropViewport Clamp(BoardCropViewport viewport)
    {
        var width = Math.Clamp(double.IsFinite(viewport.Width) ? viewport.Width : 1, 0.000001, 1);
        var height = Math.Clamp(double.IsFinite(viewport.Height) ? viewport.Height : 1, 0.000001, 1);
        var x = Math.Clamp(double.IsFinite(viewport.X) ? viewport.X : 0, 0, 1 - width);
        var y = Math.Clamp(double.IsFinite(viewport.Y) ? viewport.Y : 0, 0, 1 - height);
        return new BoardCropViewport(x, y, width, height);
    }

    private static double SafeAspect(double width, double height)
    {
        var aspect = width / Math.Max(0.000001, height);
        return double.IsFinite(aspect) && aspect > 0 ? aspect : 1;
    }
}
