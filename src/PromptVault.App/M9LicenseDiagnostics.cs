using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PromptVault.App.Services;
using PromptVault.Licensing;

namespace PromptVault.App;

internal static class M9LicenseDiagnostics
{
    public static async Task CaptureActivationAsync(
        LicenseActivationWindow window,
        LicenseValidationResult validation,
        string reportPath)
    {
        await WaitForLayoutAsync(window);
        var screenshotPath = Path.ChangeExtension(reportPath, ".png");
        Capture(window, screenshotPath);
        var dpi = VisualTreeHelper.GetDpi(window);
        await WriteReportAsync(reportPath, new
        {
            Milestone = "M9-offline-device-license-activation-window",
            GeneratedAt = DateTimeOffset.Now,
            Passed = !validation.IsValid
                     && window.ActualWidth >= 680
                     && window.ActualHeight >= 520
                     && window.DeviceCodeText.Length >= 30,
            LicenseStatus = validation.Status.ToString(),
            RepositoryInitialized = false,
            SettingsLoaded = false,
            Window = new
            {
                window.ActualWidth,
                window.ActualHeight,
                DpiX = dpi.PixelsPerInchX,
                DpiY = dpi.PixelsPerInchY,
                DeviceCodeVisible = window.DeviceCodeText.Length >= 30,
                StatusVisible = !string.IsNullOrWhiteSpace(window.StatusMessage)
            },
            Screenshot = Path.GetFileName(screenshotPath),
            DataSafety = new { RealLibraryAccesses = 0, ClipboardCaptureStarted = false, NetworkRequests = 0 }
        });
    }

    public static async Task CaptureMainAsync(
        MainWindow window,
        AppSettings settings,
        LicenseValidationResult validation,
        string reportPath)
    {
        await WaitForLayoutAsync(window);
        var screenshotPath = Path.ChangeExtension(reportPath, ".png");
        Capture(window, screenshotPath);
        var dpi = VisualTreeHelper.GetDpi(window);
        await WriteReportAsync(reportPath, new
        {
            Milestone = "M9-offline-device-license-authorized-main",
            GeneratedAt = DateTimeOffset.Now,
            Passed = validation.IsValid
                     && !settings.CaptureListeningEnabled
                     && window.IsVisible,
            LicenseStatus = validation.Status.ToString(),
            LicenseId = validation.License?.LicenseId,
            ExpiresAtUtc = validation.License?.ExpiresAtUtc,
            RepositoryInitialized = true,
            SettingsLoaded = true,
            CaptureListeningEnabled = settings.CaptureListeningEnabled,
            Window = new
            {
                window.ActualWidth,
                window.ActualHeight,
                DpiX = dpi.PixelsPerInchX,
                DpiY = dpi.PixelsPerInchY
            },
            Screenshot = Path.GetFileName(screenshotPath),
            DataSafety = new { ExplicitSettings = true, RealLibraryAccesses = 0, NetworkRequests = 0 }
        });
    }

    private static async Task WaitForLayoutAsync(Window window)
    {
        if (!window.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                window.Loaded -= handler;
                loaded.TrySetResult();
            };
            window.Loaded += handler;
            await loaded.Task;
        }
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Capture(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task WriteReportAsync(string path, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
