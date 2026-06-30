using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PromptVault.App.Services;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly Window _owner;
    private readonly CaptureCoordinator _coordinator;
    private readonly Func<IReadOnlyList<PromptVault.Core.CategoryRecord>> _categories;
    private readonly Func<PendingCapture, string, string, long?, string, Task> _save;
    private HwndSource? _source;
    private PendingCapture? _pending;
    private CaptureWindow? _captureWindow;
    private bool _processing;
    private bool _enabled = true;

    public bool IsEnabled => _enabled;

    public ClipboardMonitor(Window owner, CaptureCoordinator coordinator,
        Func<IReadOnlyList<PromptVault.Core.CategoryRecord>> categories,
        Func<PendingCapture, string, string, long?, string, Task> save)
    {
        _owner = owner;
        _coordinator = coordinator;
        _categories = categories;
        _save = save;
        owner.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && _enabled && !_processing) _ = HandleClipboardAsync();
        return IntPtr.Zero;
    }

    private async Task HandleClipboardAsync()
    {
        _processing = true;
        try
        {
            if (_pending is not null)
            {
                if (DateTimeOffset.UtcNow - _pending.CapturedAt > TimeSpan.FromMinutes(5))
                {
                    CancelPending();
                    return;
                }

                var text = TryGetClipboardText();
                if (!string.IsNullOrWhiteSpace(text)) _captureWindow?.SetPrompt(text);
                return;
            }

            var file = TryGetImageFile();
            BitmapSource? image = null;
            if (file is null) image = TryGetClipboardImage();
            if (file is null && image is null) return;

            var pending = file is not null
                ? await _coordinator.CreateFromFileAsync(file)
                : await _coordinator.CreateFromBitmapAsync(image!);
            PresentPending(pending);
        }
        catch (Exception ex)
        {
            _owner.Dispatcher.Invoke(() => ToastService.Show(_owner, FriendlyReadError(ex, "无法读取剪贴板图片")));
        }
        finally
        {
            _processing = false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) CancelPending();
    }

    public Task CaptureDroppedImageAsync(DroppedImageSource source)
    {
        if (source.FilePath is not null) return CaptureFileAsync(source.FilePath);
        if (source.Uri is not null) return CaptureUriAsync(source.Uri);
        return Task.CompletedTask;
    }

    public async Task CaptureFileAsync(string path)
    {
        if (_processing) return;
        _processing = true;
        try
        {
            var pending = await _coordinator.CreateFromFileAsync(path);
            await PresentOrReplacePendingAsync(pending);
        }
        catch (Exception ex)
        {
            _owner.Dispatcher.Invoke(() => ToastService.Show(_owner, FriendlyReadError(ex, "无法读取拖入的图片")));
        }
        finally
        {
            _processing = false;
        }
    }

    private async Task CaptureUriAsync(Uri uri)
    {
        if (_processing) return;
        _processing = true;
        try
        {
            var pending = await _coordinator.CreateFromUriAsync(uri);
            await PresentOrReplacePendingAsync(pending);
        }
        catch (Exception ex)
        {
            _owner.Dispatcher.Invoke(() => ToastService.Show(_owner, FriendlyReadError(ex, "无法读取拖入的图片链接")));
        }
        finally
        {
            _processing = false;
        }
    }

    private async Task PresentOrReplacePendingAsync(PendingCapture pending)
    {
        if (_pending is not null && _captureWindow is not null)
        {
            var previous = _pending;
            _pending = pending;
            previous.Dispose();
            await _captureWindow.ReplacePendingAsync(pending);
        }
        else
        {
            PresentPending(pending);
        }
    }

    private void PresentPending(PendingCapture pending)
    {
        var oldPending = _pending;
        var oldWindow = _captureWindow;
        _pending = null;
        _captureWindow = null;
        oldPending?.Dispose();
        if (oldWindow?.IsVisible == true) oldWindow.CloseAfterSave();

        _pending = pending;
        var window = new CaptureWindow(pending, _categories());
        _captureWindow = window;
        window.SaveRequested += SavePendingAsync;
        window.CancelRequested += CancelPending;
        window.ImageDropped += CaptureDroppedImageAsync;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_captureWindow, window) && _pending is not null) CancelPending();
        };
        window.Show();
    }

    private async Task SavePendingAsync(string prompt, string notes, long? category, string tags)
    {
        if (_pending is null) return;
        try
        {
            await _save(_pending, prompt, notes, category, tags);
            var window = _captureWindow;
            _pending = null;
            _captureWindow = null;
            window?.CloseAfterSave();
        }
        catch (Exception ex)
        {
            _captureWindow?.ShowError(FriendlyReadError(ex, "保存失败"));
        }
    }

    private void CancelPending()
    {
        var pending = _pending;
        var window = _captureWindow;
        _pending = null;
        _captureWindow = null;
        pending?.Dispose();
        if (window?.IsVisible == true) window.CloseAfterSave();
    }

    private static string? TryGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (ExternalException) { return null; }
    }

    private static string? TryGetImageFile()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return null;
            return Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(path =>
                new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        }
        catch (ExternalException) { return null; }
    }

    private static BitmapSource? TryGetClipboardImage()
    {
        try
        {
            if (!Clipboard.ContainsImage()) return null;
            var image = Clipboard.GetImage();
            image?.Freeze();
            return image;
        }
        catch (ExternalException) { return null; }
    }

    private static string FriendlyReadError(Exception ex, string prefix)
    {
        if (ex is NotSupportedException) return $"{prefix}：{ex.Message}";
        if (ex is InvalidDataException or FileFormatException or ArgumentException or InvalidOperationException)
            return $"{prefix}：这张图片格式比较特殊，暂时无法读取。";
        if (ex is HttpRequestException or TaskCanceledException)
            return $"{prefix}：图片链接下载失败，请稍后再试。";
        return $"{prefix}：{ex.Message}";
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
        }
        CancelPending();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
