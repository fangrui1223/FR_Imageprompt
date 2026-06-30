using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Animation;

namespace PromptVault.App;

public partial class CaptureWindow
{
    private static readonly string[] DropExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    private bool _previewDragActive;
    private DateTime _previewLastDragOver;
    public event Func<DroppedImageSource, Task>? ImageDropped;

    private void CapturePreviewDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        var source = GetDroppedImage(e.Data);
        e.Effects = source is null ? System.Windows.DragDropEffects.None : System.Windows.DragDropEffects.Copy;
        e.Handled = true;
        if (source is null) return;

        _previewLastDragOver = DateTime.UtcNow;
        if (_previewDragActive) return;
        _previewDragActive = true;
        ShowPreviewDropGlow();
    }

    private async void CapturePreviewDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (!_previewDragActive) return;
        var observed = _previewLastDragOver;
        await Task.Delay(100);
        if (!_previewDragActive || _previewLastDragOver > observed) return;
        _previewDragActive = false;
        HidePreviewDropGlow();
    }

    private async void CapturePreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        var source = GetDroppedImage(e.Data);
        e.Handled = true;
        if (source is null)
        {
            _previewDragActive = false;
            HidePreviewDropGlow();
            return;
        }

        _previewDragActive = false;
        await AnimatePreviewAbsorbAsync();
        if (ImageDropped is { } handler) await handler(source);
    }

    private void ShowPreviewDropGlow()
    {
        PreviewDropGlow.Visibility = Visibility.Visible;
        PreviewDropGlow.Opacity = 0;
        PreviewDropScale.ScaleX = 0.92;
        PreviewDropScale.ScaleY = 0.92;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PreviewDropGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        PreviewDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        PreviewDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        PreviewGlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(10, 38, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void HidePreviewDropGlow()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (!_previewDragActive) PreviewDropGlow.Visibility = Visibility.Collapsed;
        };
        PreviewDropGlow.BeginAnimation(OpacityProperty, fade);
    }

    private async Task AnimatePreviewAbsorbAsync()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        PreviewDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        PreviewDropScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        PreviewGlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(38, 3, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        PreviewDropGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        await Task.Delay(230);
        PreviewDropGlow.Visibility = Visibility.Collapsed;
    }

    internal static DroppedImageSource? GetDroppedImage(System.Windows.IDataObject data)
    {
        if (TryGetDroppedFile(data) is { } file) return DroppedImageSource.FromFile(file);
        if (TryGetImageUri(data) is { } uri) return DroppedImageSource.FromUri(uri);
        return null;
    }

    private static string? TryGetDroppedFile(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop) || data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(path => DropExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static Uri? TryGetImageUri(System.Windows.IDataObject data)
    {
        foreach (var text in GetPossibleTexts(data))
        {
            if (TryParseImageUri(text, requireImageExtension: true) is { } direct) return direct;
            if (TryExtractImageUriFromHtml(text) is { } fromHtml) return fromHtml;
        }

        return null;
    }

    private static IEnumerable<string> GetPossibleTexts(System.Windows.IDataObject data)
    {
        foreach (var format in new[] { System.Windows.DataFormats.Html, System.Windows.DataFormats.UnicodeText, System.Windows.DataFormats.Text, "UniformResourceLocatorW", "UniformResourceLocator" })
        {
            if (!data.GetDataPresent(format)) continue;
            var value = data.GetData(format);
            if (value is string text && !string.IsNullOrWhiteSpace(text)) yield return text;
            else if (value is MemoryStream stream)
            {
                var bytes = stream.ToArray();
                var decodedText = format.EndsWith("W", StringComparison.OrdinalIgnoreCase)
                    ? Encoding.Unicode.GetString(bytes).TrimEnd('\0')
                    : Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(decodedText)) yield return decodedText;
            }
        }
    }

    private static Uri? TryExtractImageUriFromHtml(string html)
    {
        var match = Regex.Match(html, "<img[^>]+src\\s*=\\s*['\"](?<url>[^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var decoded = WebUtility.HtmlDecode(match.Groups["url"].Value);
        return TryParseImageUri(decoded, requireImageExtension: false);
    }

    private static Uri? TryParseImageUri(string text, bool requireImageExtension)
    {
        text = WebUtility.HtmlDecode(text.Trim().Trim('"', '\'', '<', '>'));
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!requireImageExtension) return uri;
        return DropExtensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase) ? uri : null;
    }
}

public sealed record DroppedImageSource(string? FilePath, Uri? Uri)
{
    public static DroppedImageSource FromFile(string path) => new(path, null);
    public static DroppedImageSource FromUri(Uri uri) => new(null, uri);
}
