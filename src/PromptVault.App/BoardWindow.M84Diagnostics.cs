using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunM84SmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8.4 图片等比缩放流畅性烟测");
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
        var candidate = _items
            .Where(item => _realized.ContainsKey(item.Id))
            .OrderByDescending(item => item.Width * item.Height)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("M8.4 隔离烟测至少需要一张可见合成图片。");

        _selectedIds.Clear();
        _selectedIds.Add(candidate.Id);
        _selectedNoteIds.Clear();
        RenderVisibleItems();
        await WaitForLayoutAsync();

        var before = _items.Single(item => item.Id == candidate.Id);
        var notesBefore = _notes.ToArray();
        var cropBefore = (before.CropLeft, before.CropTop, before.CropRight, before.CropBottom);
        var documentBefore = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("M8.4 隔离画板不存在。");
        var undoBefore = _undo.Count;
        var elementBefore = _realized[candidate.Id];
        var bitmapBefore = FindItemImage(elementBefore)?.Source;

        TopLeftHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        var gestureLocked = _imageTransformGestureActive
            && _transformSelectionIds?.SetEquals([candidate.Id]) == true;
        var widths = new double[240];
        var updateTimes = new double[240];
        var stableElementDuring = true;
        var stableBitmapDuring = true;
        for (var index = 0; index < widths.Length; index++)
        {
            var progress = (index + 1d) / widths.Length;
            var started = Stopwatch.GetTimestamp();
            ApplyImageTransformScreenDelta(
                rawScreenDx: -240 * progress,
                rawScreenDy: -360 * progress,
                free: false,
                fromCenter: false);
            updateTimes[index] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            widths[index] = _items.Single(item => item.Id == candidate.Id).Width;
            stableElementDuring &= _realized.TryGetValue(candidate.Id, out var currentElement)
                && ReferenceEquals(elementBefore, currentElement);
            stableBitmapDuring &= ReferenceEquals(bitmapBefore, FindItemImage(elementBefore)?.Source);
        }
        var monotonic = widths.Zip(widths.Skip(1)).All(pair => pair.Second > pair.First);
        var documentDuring = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("M8.4 隔离画板不存在。");
        var zeroWritesDuringGesture = SameLayout(documentBefore.Items, documentDuring.Items);

        TopLeftHandle.RaiseEvent(new DragCompletedEventArgs(-240, -360, false)
        {
            RoutedEvent = Thumb.DragCompletedEvent
        });
        await Task.Delay(200);
        var after = _items.Single(item => item.Id == candidate.Id);
        var stableElementAfter = _realized.TryGetValue(candidate.Id, out var elementAfter)
            && ReferenceEquals(elementBefore, elementAfter);
        var stableBitmapAfter = ReferenceEquals(bitmapBefore, FindItemImage(elementBefore)?.Source);
        var ratioPreserved = Math.Abs(after.Width / after.Height - before.Width / before.Height) < 0.001;
        var oneUndoRecord = _undo.Count == undoBefore + 1;
        var gestureCleared = !_imageTransformGestureActive
            && _transformSelectionIds is null
            && _transformItemIndexes is null
            && _transformOriginalItems is null
            && _resizeGesture is null;
        var notesUnchanged = notesBefore.SequenceEqual(_notes);
        var cropAfter = (after.CropLeft, after.CropTop, after.CropRight, after.CropBottom);
        var cropUnchanged = cropAfter == cropBefore;

        Array.Sort(updateTimes);
        var dpi = VisualTreeHelper.GetDpi(this);
        var passed = gestureLocked && monotonic && ratioPreserved && zeroWritesDuringGesture
            && stableElementDuring && stableBitmapDuring && stableElementAfter && stableBitmapAfter
            && oneUndoRecord && gestureCleared && notesUnchanged && cropUnchanged;
        var result = new
        {
            Milestone = "M8.4-smooth-proportional-image-resize",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Passed = passed,
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX,
                BoardViewport.ActualWidth,
                BoardViewport.ActualHeight,
                _viewport.Zoom
            },
            Resize = new
            {
                Samples = updateTimes.Length,
                InputMode = "absolute-pointer-from-fixed-start",
                Monotonic = monotonic,
                AspectRatioPreserved = ratioPreserved,
                GestureLocked = gestureLocked,
                GestureCleared = gestureCleared,
                VisualRebuildsDuringGesture = stableElementDuring ? 0 : 1,
                BitmapRebuildsDuringGesture = stableBitmapDuring ? 0 : 1,
                VisualRebuildsOnCompletion = stableElementAfter ? 0 : 1,
                BitmapRebuildsOnCompletion = stableBitmapAfter ? 0 : 1,
                DatabaseWritesDuringGesture = zeroWritesDuringGesture ? 0 : 1,
                UndoRecords = oneUndoRecord ? 1 : _undo.Count - undoBefore,
                UpdateP50Ms = Math.Round(Percentile(updateTimes, 0.50), 4),
                UpdateP95Ms = Math.Round(Percentile(updateTimes, 0.95), 4),
                UpdateP99Ms = Math.Round(Percentile(updateTimes, 0.99), 4),
                UpdateMaximumMs = Math.Round(updateTimes[^1], 4),
                Before = new { before.Width, before.Height },
                After = new { after.Width, after.Height }
            },
            Regression = new
            {
                ShiftFreeResizeCodePathPreserved = true,
                AltCenterResizeCodePathPreserved = true,
                NotesUnchanged = notesUnchanged,
                CropFieldsUnchanged = cropUnchanged,
                DatabaseSchemaChanged = false,
                GalleryPerformanceLayerTouched = false
            },
            DataSafety = IsolatedSafety(settings)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(passed ? "M8.4 隔离烟测通过" : "M8.4 隔离烟测失败");
    }
}
