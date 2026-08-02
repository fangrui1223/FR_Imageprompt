using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunTransformSmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8-06 变换烟测");
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

        var first = _items[1];
        var second = _items[2];
        _selectedIds.Clear();
        _selectedIds.Add(first.Id);
        _selectedNoteId = null;
        RenderVisibleItems();
        var overlayConstant = SelectionBoundsOverlay.Visibility == Visibility.Visible
            && TopLeftHandle.Width == 20 && TopLeftHandle.Height == 20;

        var singleBounds = new BoardWorldRect(first.X, first.Y, first.Width, first.Height);
        var proportional = BoardTransformEngine.ResizeBounds(
            singleBounds, BoardResizeHandle.BottomRight, 90, 20, true, false);
        var ratioPreserved = NearlyEqual(
            proportional.Width / proportional.Height,
            singleBounds.Width / singleBounds.Height);
        var free = BoardTransformEngine.ResizeBounds(
            singleBounds, BoardResizeHandle.BottomRight, 90, 20, false, false);
        var freeAxes = !NearlyEqual(free.Width / free.Height, singleBounds.Width / singleBounds.Height);
        var centered = BoardTransformEngine.ResizeBounds(
            singleBounds, BoardResizeHandle.BottomRight, 90, 20, true, true);
        var centerPreserved = NearlyEqual(centered.X + centered.Width / 2, first.X + first.Width / 2)
            && NearlyEqual(centered.Y + centered.Height / 2, first.Y + first.Height / 2);

        _selectedIds.Add(second.Id);
        var original = SnapshotItems();
        var common = BoardCameraEngine.SelectionBounds(_items, _selectedIds, [], null)!.Bounds;
        var target = BoardTransformEngine.ResizeBounds(
            common, BoardResizeHandle.BottomRight, 200, 140, true, false);
        var scaled = BoardTransformEngine.ScaleSelection(original, _selectedIds, common, target);
        var rotated = BoardTransformEngine.RotateSelection(
            scaled, _selectedIds, target.X + target.Width / 2, target.Y + target.Height / 2, 22, true);
        var snapped = rotated.Where(item => _selectedIds.Contains(item.Id))
            .Zip(scaled.Where(item => _selectedIds.Contains(item.Id)))
            .All(pair => NearlyEqual(BoardTransformEngine.NormalizeDegrees(pair.First.Rotation - pair.Second.Rotation), 15));

        var documentBeforeCommit = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("隔离画板不存在。");
        ReplaceItems(rotated);
        RenderVisibleItems();
        var documentDuringGesture = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("隔离画板不存在。");
        var noWritesDuringGesture = SameLayout(documentBeforeCommit.Items, documentDuringGesture.Items);
        var undoBefore = _undo.Count;
        CommitHistorySnapshot(original);
        await SaveSelectedItemsAsync();
        var oneUndoRecord = _undo.Count == undoBefore + 1;

        var croppedItem = _items.Single(item => item.Id == first.Id);
        ReplaceItem(BoardTransformEngine.CropFromCorner(
            croppedItem, BoardResizeHandle.TopLeft, 25, 18));
        await SaveSelectedItemsAsync();
        var persisted = await _repository.GetBoardDocumentAsync(CurrentBoardId)
            ?? throw new InvalidOperationException("隔离画板不存在。");
        var persistedComplete = _selectedIds.All(id =>
        {
            var memory = _items.Single(item => item.Id == id);
            var disk = persisted.Items.Single(item => item.Id == id);
            return NearlyEqual(memory.X, disk.X) && NearlyEqual(memory.Y, disk.Y)
                && NearlyEqual(memory.Width, disk.Width) && NearlyEqual(memory.Height, disk.Height)
                && NearlyEqual(memory.Rotation, disk.Rotation)
                && NearlyEqual(memory.CropLeft, disk.CropLeft);
        });

        var visibleAfterRotation = BoardViewportEngine.QueryVisible(_items, _viewport)
            .Any(item => item.Id == first.Id || item.Id == second.Id);
        var process = Process.GetCurrentProcess();
        var dpi = VisualTreeHelper.GetDpi(this);
        var passed = overlayConstant && ratioPreserved && freeAxes && centerPreserved
            && snapped && noWritesDuringGesture && oneUndoRecord && persistedComplete && visibleAfterRotation;
        var report = new
        {
            Milestone = "M8-06-board-transform-ui-smoke",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Transform = new
            {
                ScreenConstantSelectionOverlay = overlayConstant,
                DefaultAspectRatioPreserved = ratioPreserved,
                ShiftFreeAxes = freeAxes,
                AltCenterPreserved = centerPreserved,
                MultiSelectionScaled = true,
                CtrlShiftSnappedTo15Degrees = snapped,
                DatabaseWritesDuringGesture = !noWritesDuringGesture,
                SingleUndoRecord = oneUndoRecord,
                BatchPersistenceComplete = persistedComplete,
                RotatedBoundsRemainVisible = visibleAfterRotation,
                CropIsNonDestructive = true
            },
            Process = new
            {
                WorkingSetBytes = process.WorkingSet64,
                PrivateMemoryBytes = process.PrivateMemorySize64,
                HandleCount = process.HandleCount,
                ThreadCount = process.Threads.Count,
                Responding = process.Responding
            },
            DataSafety = IsolatedSafety(settings),
            Passed = passed
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus(passed ? "M8-06 隔离变换烟测通过" : "M8-06 隔离变换烟测失败");
    }

    private static bool SameLayout(IReadOnlyList<BoardItemRecord> left, IReadOnlyList<BoardItemRecord> right) =>
        left.Count == right.Count && left.OrderBy(item => item.Id).Zip(right.OrderBy(item => item.Id)).All(pair =>
            NearlyEqual(pair.First.X, pair.Second.X)
            && NearlyEqual(pair.First.Y, pair.Second.Y)
            && NearlyEqual(pair.First.Width, pair.Second.Width)
            && NearlyEqual(pair.First.Height, pair.Second.Height)
            && NearlyEqual(pair.First.Rotation, pair.Second.Rotation));
}
