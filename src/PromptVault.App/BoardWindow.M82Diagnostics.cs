using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunM82SmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8.2 变换裁剪烟测");
        reportPath = Path.GetFullPath(reportPath);
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) => { Loaded -= handler; loaded.TrySetResult(); };
            Loaded += handler;
            await loaded.Task;
        }
        WindowState = WindowState.Maximized;
        await WaitForLayoutAsync();

        var item = _items.First(candidate => _realized.ContainsKey(candidate.Id));
        _selectedIds.Clear();
        _selectedIds.Add(item.Id);
        _selectedNoteIds.Clear();
        RenderVisibleItems();
        await WaitForLayoutAsync();

        var handles = new[] { TopLeftHandle, TopRightHandle, BottomLeftHandle, BottomRightHandle };
        var hitTests = handles.Select(handle =>
        {
            var center = handle.TransformToAncestor(BoardViewport).Transform(new Point(14, 14));
            var hit = BoardViewport.InputHitTest(center) as DependencyObject;
            return FindParent<Thumb>(hit) == handle;
        }).ToArray();
        var beforeResize = _items.Single(candidate => candidate.Id == item.Id);
        TopLeftHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        TopLeftHandle.RaiseEvent(new DragDeltaEventArgs(-42, -24) { RoutedEvent = Thumb.DragDeltaEvent });
        TopLeftHandle.RaiseEvent(new DragCompletedEventArgs(-42, -24, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Task.Delay(120);
        var afterResize = _items.Single(candidate => candidate.Id == item.Id);
        var routedResizeChanged = Math.Abs(afterResize.Width - beforeResize.Width) > 0.01
            && Math.Abs(afterResize.Width / afterResize.Height - beforeResize.Width / beforeResize.Height) < 0.001;

        var note = _notes.First();
        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        _selectedNoteIds.Add(note.Id);
        RenderVisibleItems();
        await WaitForLayoutAsync();
        var noteHandle = _realizedNotes[note.Id].ResizeHandles[^1];
        var beforeNote = _notes.Single(candidate => candidate.Id == note.Id);
        noteHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        noteHandle.RaiseEvent(new DragDeltaEventArgs(36, 28) { RoutedEvent = Thumb.DragDeltaEvent });
        noteHandle.RaiseEvent(new DragCompletedEventArgs(36, 28, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Task.Delay(120);
        var afterNote = _notes.Single(candidate => candidate.Id == note.Id);
        var routedNoteResizeChanged = afterNote.Width > beforeNote.Width && afterNote.Height > beforeNote.Height;

        _selectedNoteIds.Clear();
        _selectedIds.Clear();
        _selectedIds.Add(item.Id);
        RenderVisibleItems();
        await WaitForLayoutAsync();
        var beforeCrop = _items.Single(candidate => candidate.Id == item.Id);
        var undoBeforeCrop = _undo.Count;
        var persistedBeforeCrop = (await _repository.GetBoardDocumentAsync(CurrentBoardId))!
            .Items.Single(candidate => candidate.Id == item.Id);
        var imageSourceBefore = FindItemImage(_realized[item.Id])?.Source;
        BeginCropMode();
        var cropEntered = _cropModeActive && SelectionOutline.BorderThickness.Left == 2
            && handles.All(handle => handle.Visibility == Visibility.Collapsed);
        if (_cropModeActive)
        {
            var updateSamples = new double[120];
            for (var index = 0; index < updateSamples.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var factor = index % 4 == 0 ? 1 / BoardCropEngine.WheelStep : BoardCropEngine.WheelStep;
                _cropViewport = BoardCropEngine.ZoomAt(
                    _cropViewport,
                    _cropMinimumCover,
                    (index % 11) / 10d,
                    (index % 7) / 6d,
                    factor);
                _cropViewport = BoardCropEngine.Pan(
                    _cropViewport,
                    index % 2 == 0 ? 2 : -1,
                    index % 3 == 0 ? -2 : 1,
                    beforeCrop.Width,
                    beforeCrop.Height);
                _cropChanged = true;
                ApplyImageViewport(_realized[item.Id], beforeCrop);
                updateSamples[index] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            }
            Array.Sort(updateSamples);
            _m82CropUpdateMetrics = new CropUpdateMetrics(
                updateSamples.Length,
                Percentile(updateSamples, 0.50),
                Percentile(updateSamples, 0.95),
                Percentile(updateSamples, 0.99),
                Math.Round(updateSamples[^1], 3));
        }
        var persistedDuringCrop = (await _repository.GetBoardDocumentAsync(CurrentBoardId))!
            .Items.Single(candidate => candidate.Id == item.Id);
        var zeroWritesDuringUpdates = SameCrop(persistedBeforeCrop, persistedDuringCrop);
        var zeroBitmapRebuildsDuringUpdates = ReferenceEquals(
            imageSourceBefore,
            FindItemImage(_realized[item.Id])?.Source);
        await CommitCropModeAsync();
        var afterCrop = _items.Single(candidate => candidate.Id == item.Id);
        var cropCommittedOnce = _undo.Count == undoBeforeCrop + 1
            && (afterCrop.CropLeft != beforeCrop.CropLeft || afterCrop.CropTop != beforeCrop.CropTop
                || afterCrop.CropRight != beforeCrop.CropRight || afterCrop.CropBottom != beforeCrop.CropBottom);
        var noCroppedBitmap = FindItemImage(_realized[item.Id])?.Source is not CroppedBitmap;

        var committedCrop = afterCrop;
        BeginCropMode();
        if (_cropModeActive)
        {
            _cropViewport = BoardCropEngine.ZoomAt(_cropViewport, _cropMinimumCover, 0.5, 0.5, 2);
            _cropChanged = true;
        }
        CancelCropMode();
        var afterCancel = _items.Single(candidate => candidate.Id == item.Id);
        var cropCancelRestored = afterCancel.CropLeft == committedCrop.CropLeft
            && afterCancel.CropTop == committedCrop.CropTop
            && afterCancel.CropRight == committedCrop.CropRight
            && afterCancel.CropBottom == committedCrop.CropBottom;

        var result = new
        {
            Milestone = "M8.2-transform-crop-window",
            Passed = hitTests.All(value => value) && routedResizeChanged && routedNoteResizeChanged
                && cropEntered && cropCommittedOnce && cropCancelRestored && noCroppedBitmap
                && zeroWritesDuringUpdates && zeroBitmapRebuildsDuringUpdates,
            Dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX,
            Viewport = new { BoardViewport.ActualWidth, BoardViewport.ActualHeight, _viewport.Zoom },
            Handles = new { Count = handles.Length, HitDip = 28, HitTests = hitTests, RoutedResizeChanged = routedResizeChanged },
            Note = new { RoutedResizeChanged = routedNoteResizeChanged, afterNote.Width, afterNote.Height },
            Crop = new
            {
                Entered = cropEntered,
                Updates = _m82CropUpdateMetrics,
                DatabaseWritesDuringUpdates = zeroWritesDuringUpdates ? 0 : 1,
                BitmapRebuildsDuringUpdates = zeroBitmapRebuildsDuringUpdates ? 0 : 1,
                CommittedOnce = cropCommittedOnce,
                CancelRestored = cropCancelRestored,
                NoCroppedBitmap = noCroppedBitmap
            },
            WindowPolicy = new
            {
                CtrlWindowedMovesWindow = BoardInteractionEngine.ShouldMoveWindowWithRightDrag(true, false),
                CtrlMaximizedMovesWindow = BoardInteractionEngine.ShouldMoveWindowWithRightDrag(true, true),
                CtrlNeverPans = !BoardInteractionEngine.ShouldPanCanvasWithRightDrag(true, false)
                    && !BoardInteractionEngine.ShouldPanCanvasWithRightDrag(true, true)
            },
            ClipboardCaptureEnabled = settings.CaptureListeningEnabled,
            QuickCaptureEnabled = settings.CaptureQuickEditEnabled,
            OnlineAiEnabled = settings.OnlineAiEnabled
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(result.Passed ? "M8.2 隔离烟测通过" : "M8.2 隔离烟测失败");
    }

    private CropUpdateMetrics _m82CropUpdateMetrics = new(0, 0, 0, 0, 0);

    private static bool SameCrop(BoardItemRecord first, BoardItemRecord second) =>
        first.CropLeft == second.CropLeft && first.CropTop == second.CropTop
        && first.CropRight == second.CropRight && first.CropBottom == second.CropBottom;

    private sealed record CropUpdateMetrics(
        int Count,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaximumMs);
}
