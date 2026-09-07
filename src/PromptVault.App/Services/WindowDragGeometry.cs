using System.Windows;

namespace PromptVault.App.Services;

internal static class WindowDragGeometry
{
    public static Point Position(Point windowStart, Point screenStart, Point screenCurrent, DpiScale dpi) => new(
        windowStart.X + (screenCurrent.X - screenStart.X) / ValidScale(dpi.DpiScaleX),
        windowStart.Y + (screenCurrent.Y - screenStart.Y) / ValidScale(dpi.DpiScaleY));

    private static double ValidScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
}
