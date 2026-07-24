using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PromptVault.Core;

namespace PromptVault.App.Services;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly string[] SupportedImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    private readonly Window _owner;
    private readonly CaptureCoordinator _coordinator;
    private readonly Func<IReadOnlyList<CategoryRecord>> _categories;
    private readonly Func<PendingCapture, string, string, long?, string, Task> _save;
    private readonly SequentialEventPump<ClipboardSnapshot> _eventPump;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private HwndSource? _source;
    private PendingCapture? _pending;
    private CaptureWindow? _captureWindow;
    private uint _lastSequence;
    private bool _enabled = true;
    private int _disposed;

    public bool IsEnabled => _enabled;

    public ClipboardMonitor(
        Window owner,
        CaptureCoordinator coordinator,
        Func<IReadOnlyList<CategoryRecord>> categories,
        Func<PendingCapture, string, string, long?, string, Task> save)
    {
        _owner = owner;
        _coordinator = coordinator;
        _categories = categories;
        _save = save;
        _eventPump = new SequentialEventPump<ClipboardSnapshot>(
            HandleClipboardSnapshotAsync,
            ex => _owner.Dispatcher.BeginInvoke(() =>
                ToastService.Show(_owner, FriendlyReadError(ex, "无法处理剪贴板内容"))));
        owner.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        if (!AddClipboardFormatListener(handle))
        {
            throw new InvalidOperationException($"无法启动剪贴板监听，Windows 错误码：{Marshal.GetLastWin32Error()}。");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmClipboardUpdate || !_enabled || Volatile.Read(ref _disposed) != 0) return IntPtr.Zero;

        var sequence = GetClipboardSequenceNumber();
        if (sequence != 0 && sequence == _lastSequence) return IntPtr.Zero;
        var read = TryCaptureClipboardSnapshot(sequence);
        if (read.Snapshot is { } snapshot)
        {
            _lastSequence = sequence;
            _eventPump.TryEnqueue(snapshot);
            DevelopmentPerformanceTrace.Event("clipboard-event-enqueued", new
            {
                sequence,
                hasImage = snapshot.HasImage,
                hasText = !string.IsNullOrWhiteSpace(snapshot.Text)
            });
        }
        else if (read.WasBusy)
        {
            _ = RetryClipboardReadAsync(sequence);
        }

        return IntPtr.Zero;
    }

    private async Task RetryClipboardReadAsync(uint expectedSequence)
    {
        foreach (var delay in new[] { 20, 50, 100 })
        {
            await Task.Delay(delay);
            if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
            var currentSequence = GetClipboardSequenceNumber();
            if (expectedSequence != 0 && currentSequence != expectedSequence) return;
            var read = TryCaptureClipboardSnapshot(currentSequence);
            if (read.Snapshot is not { } snapshot)
            {
                if (!read.WasBusy) return;
                continue;
            }

            _lastSequence = currentSequence;
            _eventPump.TryEnqueue(snapshot);
            return;
        }
    }

    private async Task HandleClipboardSnapshotAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
            if (snapshot.HasImage)
            {
                var pending = snapshot.FilePath is not null
                    ? await _coordinator.CreateFromFileAsync(snapshot.FilePath, cancellationToken)
                    : await _coordinator.CreateFromBitmapAsync(snapshot.Image!, cancellationToken);
                if (!_enabled || Volatile.Read(ref _disposed) != 0)
                {
                    pending.Dispose();
                    return;
                }
                await PresentOrReplacePendingAsync(pending);
                DevelopmentPerformanceTrace.Event("clipboard-image-ready", new { snapshot.Sequence });
                return;
            }

            if (_pending is null || string.IsNullOrWhiteSpace(snapshot.Text)) return;
            if (DateTimeOffset.UtcNow - _pending.CapturedAt > TimeSpan.FromMinutes(5))
            {
                CancelPending();
                return;
            }

            _captureWindow?.SetPrompt(snapshot.Text);
            DevelopmentPerformanceTrace.Event("clipboard-prompt-applied", new { snapshot.Sequence });
        }
        catch (Exception ex)
        {
            _owner.Dispatcher.Invoke(() => ToastService.Show(_owner, FriendlyReadError(ex, "无法读取剪贴板内容")));
        }
        finally
        {
            _captureGate.Release();
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
        await _captureGate.WaitAsync();
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
            _captureGate.Release();
        }
    }

    private async Task CaptureUriAsync(Uri uri)
    {
        await _captureGate.WaitAsync();
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
            _captureGate.Release();
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

    private static ClipboardReadResult TryCaptureClipboardSnapshot(uint sequence)
    {
        try
        {
            var file = TryGetImageFile();
            BitmapSource? image = null;
            if (file is null) image = TryGetClipboardImage();
            var text = TryGetClipboardText();
            if (file is null && image is null && string.IsNullOrWhiteSpace(text))
            {
                return new ClipboardReadResult(null, false);
            }

            return new ClipboardReadResult(new ClipboardSnapshot(sequence, file, image, text), false);
        }
        catch (ExternalException)
        {
            return new ClipboardReadResult(null, true);
        }
    }

    private static string? TryGetClipboardText() =>
        Clipboard.ContainsText() ? Clipboard.GetText() : null;

    private static string? TryGetImageFile()
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        return Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(path =>
            SupportedImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static BitmapSource? TryGetClipboardImage()
    {
        if (!Clipboard.ContainsImage()) return null;
        var image = Clipboard.GetImage();
        image?.Freeze();
        return image;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.SourceInitialized -= OnSourceInitialized;
        if (_source is not null)
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
        }
        _eventPump.Stop();
        CancelPending();
    }

    private sealed record ClipboardSnapshot(uint Sequence, string? FilePath, BitmapSource? Image, string? Text)
    {
        public bool HasImage => FilePath is not null || Image is not null;
    }

    private sealed record ClipboardReadResult(ClipboardSnapshot? Snapshot, bool WasBusy);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
