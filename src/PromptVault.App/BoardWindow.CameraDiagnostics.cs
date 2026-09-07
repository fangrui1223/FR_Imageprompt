using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunCameraSmokeAsync(
        string reportPath,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        reportPath = Path.GetFullPath(reportPath);
        var isolatedSegment =
            $"{Path.DirectorySeparatorChar}.m8-isolated{Path.DirectorySeparatorChar}";
        var isolatedFixture = _repository.Paths.Root.Contains(
            isolatedSegment,
            StringComparison.OrdinalIgnoreCase);
        if (!isolatedFixture
            || !Path.GetFullPath(settings.LibraryRoot).Equals(
                _repository.Paths.Root,
                StringComparison.OrdinalIgnoreCase)
            || settings.CaptureListeningEnabled
            || settings.CaptureQuickEditEnabled
            || settings.OnlineAiEnabled)
        {
            throw new InvalidOperationException("M8-01 相机烟测拒绝使用非隔离设置。");
        }
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                Loaded -= handler;
                loaded.TrySetResult();
            };
            Loaded += handler;
            await loaded.Task;
        }

        WindowState = WindowState.Maximized;
        await WaitForLayoutAsync();
        var initial = CurrentViewportSize();
        var initialCenter = ViewportWorldCenter(initial);

        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        RequireCameraKey(Key.Space, ModifierKeys.None);
        await WaitForCameraSettledAsync();
        var noSelectionSpace = _viewport;

        RequireCameraKey(Key.Space, ModifierKeys.Control);
        await WaitForCameraSettledAsync();
        var fullFocus = _viewport;
        var fullBounds = BoardCameraEngine.ContentBounds(_items, _notes);
        var fullVisible = BoundsAreVisible(fullBounds, fullFocus);

        RequireCameraKey(Key.Space, ModifierKeys.None);
        await WaitForCameraSettledAsync();
        var restoredFromFull = _viewport;

        var rotatedItem = _items
            .Where(item => Math.Abs(item.Rotation % 90) > 0.01)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .First();
        _selectedIds.Clear();
        _selectedIds.Add(rotatedItem.Id);
        _selectedNoteIds.Clear();
        RenderVisibleItems();
        FocusItemFromDoubleClick(rotatedItem.Id);
        await WaitForCameraSettledAsync();
        var rotatedFocus = _viewport;
        var rotatedBounds = BoardBoundsResult.From(BoardCameraEngine.ItemBounds(rotatedItem));
        var rotatedVisible = BoundsAreVisible(rotatedBounds, rotatedFocus);

        await PersistViewNowAsync(force: true);
        var persistedDuringFocus = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("隔离画板在保存后不可读取。");
        var focusDidNotPollutePersistence =
            NearlyEqual(persistedDuringFocus.Board.ViewOffsetX, initial.OffsetX)
            && NearlyEqual(persistedDuringFocus.Board.ViewOffsetY, initial.OffsetY)
            && NearlyEqual(persistedDuringFocus.Board.Zoom, initial.Zoom);

        var focusedId = _focusController.Target?.SingleItemId;
        RequireCameraKey(Key.Right, ModifierKeys.None);
        await WaitForCameraSettledAsync();
        var adjacentId = _focusController.Target?.SingleItemId;
        var adjacentChanged = focusedId is not null
            && adjacentId is not null
            && focusedId != adjacentId;

        RequireCameraKey(Key.Space, ModifierKeys.Control);
        await WaitForCameraSettledAsync();
        var forcedFull = _viewport;
        _selectedIds.Clear();
        RequireCameraKey(Key.Space, ModifierKeys.None);
        await WaitForCameraSettledAsync();
        var restoredAfterNavigation = _viewport;

        _selectedIds.Clear();
        _selectedIds.Add(rotatedItem.Id);
        RequireCameraKey(Key.Space, ModifierKeys.None);
        await WaitForCameraSettledAsync();
        WindowState = WindowState.Normal;
        Width = 1200;
        Height = 760;
        await WaitForLayoutAsync();
        var resizedFocus = _viewport;
        var resizedVisible = BoundsAreVisible(rotatedBounds, resizedFocus);
        _selectedIds.Clear();
        RequireCameraKey(Key.Space, ModifierKeys.None);
        await WaitForCameraSettledAsync();
        var restoredAfterResize = _viewport;
        var restoredCenter = ViewportWorldCenter(restoredAfterResize);

        _selectedIds.Add(rotatedItem.Id);
        ResetViewToOneHundredPercent();
        await WaitForCameraSettledAsync();
        var oneHundred = _viewport;
        var oneHundredCenter = ViewportWorldCenter(oneHundred);
        var rotatedCenterX = rotatedBounds.Bounds.X + rotatedBounds.Bounds.Width / 2;
        var rotatedCenterY = rotatedBounds.Bounds.Y + rotatedBounds.Bounds.Height / 2;
        var oneHundredCenteredSelection = NearlyEqual(oneHundred.Zoom, 1)
            && NearlyEqual(oneHundredCenter.X, rotatedCenterX)
            && NearlyEqual(oneHundredCenter.Y, rotatedCenterY);
        RestoreWorkingView();
        await WaitForCameraSettledAsync();
        var restoredAfterOneHundred = _viewport;

        var dpi = VisualTreeHelper.GetDpi(this);
        var process = Process.GetCurrentProcess();
        var passed =
            SameCamera(initial, noSelectionSpace)
            && fullVisible
            && rotatedVisible
            && resizedVisible
            && adjacentChanged
            && focusDidNotPollutePersistence
            && SameCamera(initial, restoredFromFull)
            && SameCamera(initial, restoredAfterNavigation)
            && NearlyEqual(initial.Zoom, restoredAfterResize.Zoom)
            && NearlyEqual(initialCenter.X, restoredCenter.X)
            && NearlyEqual(initialCenter.Y, restoredCenter.Y)
            && oneHundredCenteredSelection
            && NearlyEqual(initial.Zoom, restoredAfterOneHundred.Zoom);
        var report = new
        {
            Milestone = "M8-01-board-camera-ui-smoke",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Initial = CameraMetrics(initial),
            EmptySelectionSpace = new
            {
                Camera = CameraMetrics(noSelectionSpace),
                NoOpWithoutSnapshot = SameCamera(initial, noSelectionSpace)
            },
            FullFocus = new
            {
                Camera = CameraMetrics(fullFocus),
                AllRotatedBoundsVisible = fullVisible
            },
            FullRestore = new
            {
                Camera = CameraMetrics(restoredFromFull),
                ExactCameraRestored = SameCamera(initial, restoredFromFull)
            },
            RotatedItemFocus = new
            {
                ItemRotation = rotatedItem.Rotation,
                Camera = CameraMetrics(rotatedFocus),
                RotatedBoundsVisible = rotatedVisible
            },
            PersistenceDuringFocus = new
            {
                SavedWorkingCamera = focusDidNotPollutePersistence
            },
            AdjacentNavigation = new
            {
                FromItemId = focusedId,
                ToItemId = adjacentId,
                Changed = adjacentChanged
            },
            ForcedFullFocus = new
            {
                Camera = CameraMetrics(forcedFull),
                RestoreKeptOriginalWorkingCamera = SameCamera(initial, restoredAfterNavigation)
            },
            ResizeDuringFocus = new
            {
                Width = resizedFocus.Width,
                Height = resizedFocus.Height,
                Camera = CameraMetrics(resizedFocus),
                RotatedBoundsVisible = resizedVisible,
                RestoredOriginalWorldCenter = NearlyEqual(initialCenter.X, restoredCenter.X)
                    && NearlyEqual(initialCenter.Y, restoredCenter.Y),
                RestoredOriginalZoom = NearlyEqual(initial.Zoom, restoredAfterResize.Zoom)
            },
            OneHundredPercent = new
            {
                Camera = CameraMetrics(oneHundred),
                CenteredSelection = oneHundredCenteredSelection,
                EscapeRestoredOriginalZoom = NearlyEqual(initial.Zoom, restoredAfterOneHundred.Zoom)
            },
            Process = new
            {
                WorkingSetBytes = process.WorkingSet64,
                PrivateMemoryBytes = process.PrivateMemorySize64,
                HandleCount = process.HandleCount,
                ThreadCount = process.Threads.Count,
                Responding = process.Responding
            },
            DataSafety = new
            {
                SyntheticFixtureOnly = isolatedFixture,
                settings.CaptureListeningEnabled,
                settings.CaptureQuickEditEnabled,
                settings.OnlineAiEnabled,
                RealLibraryOpened = false,
                ClipboardUsed = false,
                NetworkUsed = false,
                ApiKeyUsed = false
            },
            Passed = passed
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(passed ? "M8-01 隔离相机烟测通过" : "M8-01 隔离相机烟测失败");
    }

    private async Task WaitForCameraSettledAsync()
    {
        var timeout = Stopwatch.StartNew();
        while (_cameraAnimationCancellation is not null)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(3))
                throw new TimeoutException("画板相机动画未在 3 秒内完成。");
            await Task.Delay(20);
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RequireCameraKey(Key key, ModifierKeys modifiers)
    {
        if (!HandleCameraKey(key, modifiers, isRepeat: false))
            throw new InvalidOperationException($"相机键 {key} 未被处理。");
    }

    private async Task WaitForLayoutAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        await Task.Delay(120);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static (double X, double Y) ViewportWorldCenter(BoardViewport viewport) =>
        BoardViewportEngine.ScreenToWorld(
            viewport,
            viewport.Width / 2,
            viewport.Height / 2);

    private static bool BoundsAreVisible(
        BoardBoundsResult bounds,
        BoardViewport viewport)
    {
        if (!bounds.HasValue) return true;
        const double tolerance = 0.75;
        var left = bounds.Bounds.X * viewport.Zoom + viewport.OffsetX;
        var top = bounds.Bounds.Y * viewport.Zoom + viewport.OffsetY;
        var right = (bounds.Bounds.X + bounds.Bounds.Width) * viewport.Zoom + viewport.OffsetX;
        var bottom = (bounds.Bounds.Y + bounds.Bounds.Height) * viewport.Zoom + viewport.OffsetY;
        return left >= -tolerance
            && top >= -tolerance
            && right <= viewport.Width + tolerance
            && bottom <= viewport.Height + tolerance;
    }

    private static bool SameCamera(BoardViewport left, BoardViewport right) =>
        NearlyEqual(left.OffsetX, right.OffsetX)
        && NearlyEqual(left.OffsetY, right.OffsetY)
        && NearlyEqual(left.Zoom, right.Zoom)
        && NearlyEqual(left.Width, right.Width)
        && NearlyEqual(left.Height, right.Height);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.000001;

    private static object CameraMetrics(BoardViewport viewport) => new
    {
        viewport.OffsetX,
        viewport.OffsetY,
        viewport.Zoom,
        viewport.Width,
        viewport.Height
    };
}
