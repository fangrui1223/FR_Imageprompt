using System.Windows.Interop;
using PromptVault.Core;

namespace PromptVault.App;

public partial class MainWindow
{
    private HwndSource? _handoffSource;
    private bool _handoffInputFrozen;
    private bool _handoffWasIndexing;
    private Task _activeGalleryRefresh = Task.CompletedTask;

    internal async Task<MainWindowSnapshot> BeginSnapshotTransferAsync()
    {
        _handoffInputFrozen = true;
        _handoffSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _handoffSource?.AddHook(BlockHandoffInput);
        if (_searchTimer.IsEnabled)
        {
            _searchTimer.Stop();
            await RefreshAsync();
        }
        Task refresh;
        do { refresh = _activeGalleryRefresh; await refresh; } while (!ReferenceEquals(refresh, _activeGalleryRefresh));
        var snapshot = CreateSnapshot();
        _galleryRowsTransferredOut = true;
        _handoffWasIndexing = _isFastBrowseIndexing;
        _loadCancellation?.Cancel();
        _resizeTimer.Stop();
        _appearanceReflowTimer?.Stop();
        StopFastBrowseBackground();
        return snapshot;
    }

    private IntPtr BlockHandoffInput(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_handoffInputFrozen) return IntPtr.Zero;
        if (message == 0x0084) // WM_NCHITTEST: no resize/caption drag during preparation.
        {
            handled = true;
            return new IntPtr(1);
        }
        handled = (message is 0x0010 or 0x0112) && !_allowClose // User close/move; permit the committed retirement.
            || message is >= 0x0100 and <= 0x0109
            || message is >= 0x0200 and <= 0x020E
            || message is >= 0x0240 and <= 0x024F;
        return IntPtr.Zero;
    }

    private void ReleaseHandoffInput()
    {
        _handoffSource?.RemoveHook(BlockHandoffInput);
        _handoffSource = null;
        _handoffInputFrozen = false;
    }

    internal void CancelSnapshotTransfer()
    {
        _galleryRowsTransferredOut = false;
        ReleaseHandoffInput();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _fastBrowseLease = _fastBrowseGenerations.Begin(_loadCancellation.Token);
        if (_handoffWasIndexing && _activeSearch is { } search)
        {
            StartFastBrowseBackground(search, _fastBrowseLease);
        }
        else
        {
            QueueNextPageIfNeeded();
            QueueRollingWarmup();
        }
        QueueThumbnailPriorityRefresh();
        _handoffWasIndexing = false;
    }
}
