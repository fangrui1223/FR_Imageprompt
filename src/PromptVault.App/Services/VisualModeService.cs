using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;

namespace PromptVault.App.Services;

public enum MotionToken
{
    Micro,
    Fast,
    Standard,
    Panel,
    Slow
}

public static class VisualModeService
{
    private static readonly IReadOnlyDictionary<MotionToken, TimeSpan> NormalDurations =
        new Dictionary<MotionToken, TimeSpan>
        {
            [MotionToken.Micro] = TimeSpan.FromMilliseconds(120),
            [MotionToken.Fast] = TimeSpan.FromMilliseconds(160),
            [MotionToken.Standard] = TimeSpan.FromMilliseconds(200),
            [MotionToken.Panel] = TimeSpan.FromMilliseconds(180),
            [MotionToken.Slow] = TimeSpan.FromMilliseconds(260)
        };

    public static bool IsTransparent { get; private set; }
    public static bool IsReducedMotion { get; private set; }

    public static void Apply(bool transparent, bool reducedMotion)
    {
        var resources = System.Windows.Application.Current?.Resources
            ?? throw new InvalidOperationException("视觉模式只能在 WPF 应用启动后应用。");

        IsTransparent = transparent;
        IsReducedMotion = reducedMotion;

        SetBrush(resources, "WindowSurfaceBrush", transparent ? Colors.Transparent : Color.FromArgb(242, 7, 11, 17));
        SetBrush(resources, "WindowBorderBrush", transparent ? Colors.Transparent : Color.FromArgb(53, 66, 83, 99));
        SetBrush(resources, "CardSurfaceBrush", transparent ? Colors.Transparent : Color.FromRgb(17, 23, 32));
        SetBrush(resources, "CardBorderBrush", transparent ? Colors.Transparent : Color.FromRgb(45, 57, 71));
        SetBrush(resources, "ImageWellBrush", transparent ? Colors.Transparent : Color.FromRgb(5, 7, 10));
        SetBrush(resources, "CardLabelBrush", transparent ? Colors.Transparent : Color.FromArgb(227, 11, 16, 23));
        SetBrush(resources, "CardTextBrush", transparent ? Colors.Transparent : Color.FromRgb(241, 245, 249));
        SetBrush(resources, "CardMetaTextBrush", transparent ? Colors.Transparent : Color.FromRgb(147, 161, 178));
        SetBrush(resources, "HeaderHintBrush", transparent ? Colors.Transparent : Color.FromRgb(99, 114, 134));
        SetBrush(resources, "StatusHintBrush", transparent ? Colors.Transparent : Color.FromRgb(147, 161, 178));
        SetBrush(resources, "TextFieldBrush", transparent ? Colors.Transparent : Color.FromArgb(194, 12, 17, 24));
        SetBrush(resources, "TextFieldBorderBrush", transparent ? Colors.Transparent : Color.FromRgb(61, 76, 94));
        SetBrush(resources, "ImmersiveBackdropBrush", transparent ? Colors.Transparent : Color.FromArgb(252, 5, 8, 12));

        resources["TopPanelBrush"] = transparent ? Brushes.Transparent : CreateTopPanelBrush();
        resources["LeftPanelBrush"] = transparent ? Brushes.Transparent : CreateLeftPanelBrush();
        resources["FloatingPanelShadow"] = transparent ? null : CreatePanelShadow(false);
        resources["SidePanelShadow"] = transparent ? null : CreatePanelShadow(true);

        foreach (var token in NormalDurations.Keys)
        {
            resources[DurationResourceKey(token)] = new Duration(ResolveDuration(token, reducedMotion));
        }
    }

    public static TimeSpan Motion(MotionToken token) => ResolveDuration(token, IsReducedMotion);

    public static TimeSpan ResolveDuration(MotionToken token, bool reducedMotion)
    {
        if (reducedMotion) return TimeSpan.Zero;
        return NormalDurations.TryGetValue(token, out var duration)
            ? duration
            : throw new ArgumentOutOfRangeException(nameof(token));
    }

    public static Brush ResourceBrush(string key)
    {
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? Brushes.Transparent;
    }

    public static Effect? ResourceEffect(string key)
    {
        return System.Windows.Application.Current?.TryFindResource(key) as Effect;
    }

    private static string DurationResourceKey(MotionToken token) => token switch
    {
        MotionToken.Micro => "MotionMicroDuration",
        MotionToken.Fast => "MotionFastDuration",
        MotionToken.Standard => "MotionStandardDuration",
        MotionToken.Panel => "MotionPanelDuration",
        MotionToken.Slow => "MotionSlowDuration",
        _ => throw new ArgumentOutOfRangeException(nameof(token))
    };

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static Brush CreateTopPanelBrush() => CreateGradient(
        (Color.FromArgb(245, 16, 21, 29), 0),
        (Color.FromArgb(234, 24, 34, 46), 0.55),
        (Color.FromArgb(240, 11, 16, 23), 1));

    private static Brush CreateLeftPanelBrush() => CreateGradient(
        (Color.FromArgb(245, 16, 21, 29), 0),
        (Color.FromArgb(232, 24, 34, 46), 0.55),
        (Color.FromArgb(240, 11, 16, 23), 1));

    private static Brush CreateGradient(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    private static Effect CreatePanelShadow(bool side)
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = side ? 32 : 28,
            ShadowDepth = 8,
            Direction = side ? 0 : 315,
            Opacity = side ? 0.46 : 0.42,
            Color = Colors.Black
        };
        effect.Freeze();
        return effect;
    }
}
