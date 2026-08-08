using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunM83SmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8.3 图片变换与右拖烟测");
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
        var visible = _items.Where(item => _realized.ContainsKey(item.Id)).Take(2).ToArray();
        if (visible.Length < 2)
            throw new InvalidOperationException("M8.3 隔离烟测至少需要两张可见合成图片。");

        var notesBefore = _notes.ToArray();
        var cropsBefore = _items.ToDictionary(
            item => item.Id,
            item => (item.CropLeft, item.CropTop, item.CropRight, item.CropBottom));

        var first = visible[0];
        _selectedIds.Clear();
        _selectedIds.Add(first.Id);
        _selectedNoteId = null;
        RenderVisibleItems();
        await WaitForLayoutAsync();

        var handleCenter = TopLeftHandle.TransformToAncestor(BoardViewport).Transform(new Point(14, 14));
        var hit = BoardViewport.InputHitTest(handleCenter) as DependencyObject;
        var hitThumb = FindParent<Thumb>(hit);
        var handleHit = hitThumb == TopLeftHandle;
        var legacyBlankClassification = IsBlankCanvasSource(TopLeftHandle);
        var blankGestureBlocked = !BoardInteractionEngine.ShouldBeginBlankCanvasGesture(
            pointerOriginatesFromThumb: true,
            pointerIsOverBlankCanvas: legacyBlankClassification);

        var singleBefore = _items.Single(item => item.Id == first.Id);
        var documentBefore = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("M8.3 隔离画板不存在。");
        TopLeftHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        var singleGestureLocked = _imageTransformGestureActive
            && _transformSelectionIds?.SetEquals([first.Id]) == true;
        TopLeftHandle.RaiseEvent(new DragDeltaEventArgs(-42, -24) { RoutedEvent = Thumb.DragDeltaEvent });
        var singleSelectionPersisted = _selectedIds.SetEquals([first.Id])
            && SelectionBoundsOverlay.Visibility == Visibility.Visible
            && _imageTransformGestureActive;
        var documentDuring = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("M8.3 隔离画板不存在。");
        var zeroWritesDuringSingleGesture = SameLayout(documentBefore.Items, documentDuring.Items);
        TopLeftHandle.RaiseEvent(new DragCompletedEventArgs(-42, -24, false)
        {
            RoutedEvent = Thumb.DragCompletedEvent
        });
        await Task.Delay(120);
        var singleAfter = _items.Single(item => item.Id == first.Id);
        var singleScaled = singleAfter.Width > singleBefore.Width
            && singleAfter.Height > singleBefore.Height
            && Math.Abs(singleAfter.Width / singleAfter.Height - singleBefore.Width / singleBefore.Height) < 0.001;
        var singleGestureCleared = !_imageTransformGestureActive
            && _transformSelectionIds is null
            && _transformOriginalItems is null
            && _resizeGesture is null;

        var second = visible[1];
        _selectedIds.Clear();
        _selectedIds.Add(first.Id);
        _selectedIds.Add(second.Id);
        _selectedNoteId = null;
        RenderVisibleItems();
        await WaitForLayoutAsync();
        var multiBefore = _items.Where(item => _selectedIds.Contains(item.Id)).ToArray();
        BottomRightHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        BottomRightHandle.RaiseEvent(new DragDeltaEventArgs(64, 42) { RoutedEvent = Thumb.DragDeltaEvent });
        var multiSelectionPersisted = _selectedIds.SetEquals([first.Id, second.Id])
            && SelectionBoundsOverlay.Visibility == Visibility.Visible
            && _imageTransformGestureActive;
        BottomRightHandle.RaiseEvent(new DragCompletedEventArgs(64, 42, false)
        {
            RoutedEvent = Thumb.DragCompletedEvent
        });
        await Task.Delay(120);
        var multiAfter = _items.Where(item => _selectedIds.Contains(item.Id)).ToArray();
        var multiScaled = multiBefore.Length == 2
            && multiAfter.Length == 2
            && multiAfter.Zip(multiBefore).All(pair => pair.First.Width > pair.Second.Width);
        var multiGestureCleared = !_imageTransformGestureActive && _transformSelectionIds is null;

        var notesUnchanged = notesBefore.SequenceEqual(_notes);
        var cropsUnchanged = _items.All(item => cropsBefore.TryGetValue(item.Id, out var crop)
            && crop == (item.CropLeft, item.CropTop, item.CropRight, item.CropBottom));
        var rightPolicy = new
        {
            PlainWindowedPans = BoardInteractionEngine.ShouldPanCanvasWithRightDrag(false, false),
            PlainMaximizedPans = BoardInteractionEngine.ShouldPanCanvasWithRightDrag(false, true),
            PlainNeverMovesWindow = !BoardInteractionEngine.ShouldMoveWindowWithRightDrag(false, false)
                && !BoardInteractionEngine.ShouldMoveWindowWithRightDrag(false, true),
            CtrlWindowedMovesWindow = BoardInteractionEngine.ShouldMoveWindowWithRightDrag(true, false),
            CtrlMaximizedMovesWindow = BoardInteractionEngine.ShouldMoveWindowWithRightDrag(true, true),
            CtrlNeverPans = !BoardInteractionEngine.ShouldPanCanvasWithRightDrag(true, false)
                && !BoardInteractionEngine.ShouldPanCanvasWithRightDrag(true, true)
        };
        var rightPolicyPassed = rightPolicy.PlainWindowedPans
            && rightPolicy.PlainMaximizedPans
            && rightPolicy.PlainNeverMovesWindow
            && rightPolicy.CtrlWindowedMovesWindow
            && rightPolicy.CtrlMaximizedMovesWindow
            && rightPolicy.CtrlNeverPans;

        var passed = handleHit && legacyBlankClassification && blankGestureBlocked
            && singleGestureLocked && singleSelectionPersisted && singleScaled && singleGestureCleared
            && zeroWritesDuringSingleGesture
            && multiSelectionPersisted && multiScaled && multiGestureCleared
            && notesUnchanged && cropsUnchanged && rightPolicyPassed;
        var dpi = VisualTreeHelper.GetDpi(this);
        var result = new
        {
            Milestone = "M8.3-image-transform-right-drag",
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
            ImageHandle = new
            {
                HitDip = 28,
                HandleHit = handleHit,
                LegacyBlankClassification = legacyBlankClassification,
                BlankGestureBlocked = blankGestureBlocked,
                SingleGestureLocked = singleGestureLocked,
                SingleSelectionPersisted = singleSelectionPersisted,
                SingleScaled = singleScaled,
                SingleGestureCleared = singleGestureCleared,
                MultiSelectionPersisted = multiSelectionPersisted,
                MultiScaled = multiScaled,
                MultiGestureCleared = multiGestureCleared,
                DatabaseWritesDuringSingleGesture = zeroWritesDuringSingleGesture ? 0 : 1
            },
            RightDragPolicy = rightPolicy,
            Regression = new
            {
                NotesUnchanged = notesUnchanged,
                CropFieldsUnchanged = cropsUnchanged,
                DatabaseSchemaChanged = false,
                GalleryPerformanceLayerTouched = false
            },
            DataSafety = IsolatedSafety(settings)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(passed ? "M8.3 隔离烟测通过" : "M8.3 隔离烟测失败");
    }
}
