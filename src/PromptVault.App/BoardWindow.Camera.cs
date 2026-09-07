using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private const double CameraAnimationMilliseconds = 180;
    private static readonly TimeSpan ManualWheelGestureDelay = TimeSpan.FromMilliseconds(250);

    private void ToggleSelectionFocus()
    {
        if (_selectedIds.Count == 0 && _selectedNoteIds.Count == 0)
        {
            CompleteManualWheelGesture();
            if (RestoreWorkingView()) return;
            if (_manualCameraHistory.Toggle(
                    Math.Max(1, BoardViewport.ActualWidth),
                    Math.Max(1, BoardViewport.ActualHeight)) is { } manualTarget)
            {
                ApplyManualCameraViewport(manualTarget);
                SetStatus("已切换到上一次手工视野");
            }
            else
            {
                SetStatus("当前没有可返回的手工视野");
            }
            return;
        }
        var target = CurrentSelectionFocusTarget();
        var transition = _focusController.FocusOrRestoreSameTarget(
            CurrentViewportSize(),
            target,
            BoundsForTarget(target));
        StartCameraTransition(
            transition,
            transition.IsRestore ? "已恢复进入前视野" : FocusStatus(target));
    }

    private void FocusFullBoard()
    {
        var target = BoardFocusTarget.FullBoard();
        var transition = _focusController.ForceFocus(
            CurrentViewportSize(),
            target,
            BoundsForTarget(target));
        StartCameraTransition(transition, "已聚焦完整画板");
    }

    private void FocusItemFromDoubleClick(long itemId)
    {
        var target = BoardFocusTarget.SingleItem(itemId);
        var transition = _focusController.FocusOrRestoreSameTarget(
            CurrentViewportSize(),
            target,
            BoundsForTarget(target));
        StartCameraTransition(
            transition,
            transition.IsRestore ? "已恢复原视野" : "已聚焦所选图片");
    }

    private void NavigateFocusedItem(int direction)
    {
        if (_focusController.Target?.SingleItemId is not { } currentId) return;
        var nextId = BoardCameraEngine.AdjacentItemId(_items, currentId, direction);
        if (nextId is null || nextId == currentId) return;
        _selectedIds.Clear();
        _selectedIds.Add(nextId.Value);
        _selectedNoteIds.Clear();
        RenderVisibleItems();
        var target = BoardFocusTarget.SingleItem(nextId.Value);
        var transition = _focusController.ForceFocus(
            CurrentViewportSize(),
            target,
            BoundsForTarget(target));
        StartCameraTransition(
            transition,
            direction > 0 ? "已聚焦下一张图片" : "已聚焦上一张图片");
    }

    private bool HandleCameraKey(
        Key key,
        ModifierKeys modifiers,
        bool isRepeat)
    {
        if (key == Key.Space)
        {
            if (!isRepeat)
            {
                if (modifiers.HasFlag(ModifierKeys.Control))
                    FocusFullBoard();
                else
                    ToggleSelectionFocus();
            }
            return true;
        }
        if ((key == Key.Left || key == Key.Right) && _focusController.IsActive)
        {
            NavigateFocusedItem(key == Key.Right ? 1 : -1);
            return true;
        }
        return false;
    }

    private BoardFocusTarget CurrentSelectionFocusTarget()
    {
        if (_selectedIds.Count == 0 && _selectedNoteIds.Count == 0)
            return BoardFocusTarget.FullBoard();
        return BoardFocusTarget.Selection(_selectedIds, _selectedNoteIds);
    }

    private BoardBoundsResult BoundsForTarget(BoardFocusTarget target)
    {
        if (target.Kind == BoardFocusTargetKind.FullBoard)
            return BoardCameraEngine.ContentBounds(_items, _notes);
        return BoardCameraEngine.SelectionBounds(
            _items,
            target.ItemIds.ToHashSet(),
            _notes,
            target.NoteIds.ToHashSet());
    }

    private bool RestoreWorkingView()
    {
        var transition = _focusController.RestoreIfAvailable(CurrentViewportSize());
        if (transition is null)
        {
            SetStatus("当前没有可恢复的临时视图");
            return false;
        }
        StartCameraTransition(transition.Value, "已恢复进入前视野");
        return true;
    }

    private void ResetViewToOneHundredPercent()
    {
        var target = CurrentSelectionFocusTarget();
        var bounds = target.Kind == BoardFocusTargetKind.FullBoard
            ? BoardCameraEngine.ContentBounds(_items, _notes)
            : BoundsForTarget(target);
        var current = CurrentViewportSize();
        var end = BoardCameraEngine.AtOneHundredPercent(bounds, current.Width, current.Height);
        var transition = _focusController.ForceViewport(current, target, end);
        StartCameraTransition(transition, "画板已恢复到 100%，按 Esc 返回");
    }

    private BoardViewport CurrentViewportSize() => _viewport with
    {
        Width = Math.Max(1, BoardViewport.ActualWidth),
        Height = Math.Max(1, BoardViewport.ActualHeight)
    };

    private void StartCameraTransition(
        BoardCameraTransition transition,
        string completedStatus)
    {
        CancelCameraAnimation();
        SetStatus(completedStatus);
        BeginProgressiveFocusLoad(transition);
        _ = AnimateCameraAsync(transition);
    }

    private async Task AnimateCameraAsync(BoardCameraTransition transition)
    {
        var cancellation = new CancellationTokenSource();
        _cameraAnimationCancellation = cancellation;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed.TotalMilliseconds < CameraAnimationMilliseconds)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_focusController.Generation != transition.Generation) return;
                var progress = stopwatch.Elapsed.TotalMilliseconds / CameraAnimationMilliseconds;
                var eased = progress * progress * (3 - 2 * progress);
                _viewport = BoardCameraEngine.Interpolate(
                    transition.Start,
                    transition.End,
                    eased);
                ApplyViewportMatrix();
                RenderVisibleItems();
                await Task.Delay(16, cancellation.Token);
            }
            if (_focusController.Generation != transition.Generation) return;
            _viewport = transition.End;
            ApplyViewportMatrix();
            RenderVisibleItems();
            _focusController.CompleteTransition(transition.Generation);
            SetRealizedBitmapScalingMode(BitmapScalingMode.HighQuality);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-camera", "Board camera animation failed.", ex);
        }
        finally
        {
            if (ReferenceEquals(_cameraAnimationCancellation, cancellation))
            {
                _cameraAnimationCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private bool TryRecalculateFocusForViewport()
    {
        if (BoardViewport.ActualWidth <= 0 || BoardViewport.ActualHeight <= 0) return false;
        BoardCameraTransition transition;
        if (_focusController.IsActive && _focusController.Target is { } target)
        {
            transition = _focusController.Refit(
                CurrentViewportSize(),
                BoundsForTarget(target));
        }
        else if (_focusController.Transition?.IsRestore == true)
        {
            transition = _focusController.ResizePendingRestore(CurrentViewportSize());
        }
        else
        {
            return false;
        }

        CancelCameraAnimation();
        _viewport = transition.End;
        ApplyViewportMatrix();
        _focusController.CompleteTransition(transition.Generation);
        return true;
    }

    private void CancelCameraAnimation()
    {
        var cancellation = _cameraAnimationCancellation;
        _cameraAnimationCancellation = null;
        cancellation?.Cancel();
    }

    private void InterruptCameraAnimation()
    {
        CancelCameraAnimation();
        CancelProgressiveFocusLoad();
        _focusController.InterruptTransition();
    }

    private void BeginManualCameraGesture()
    {
        CompleteManualWheelGesture();
        InterruptCameraAnimation();
        _focusController.Reset();
    }

    private void BeginManualWheelGesture()
    {
        if (_manualWheelGestureStart is null)
        {
            InterruptCameraAnimation();
            _focusController.Reset();
            _manualWheelGestureStart = _viewport;
        }
        var previous = _manualWheelGestureCancellation;
        var cancellation = new CancellationTokenSource();
        _manualWheelGestureCancellation = cancellation;
        previous?.Cancel();
        previous?.Dispose();
        _ = CompleteManualWheelGestureAfterDelayAsync(cancellation);
    }

    private async Task CompleteManualWheelGestureAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(ManualWheelGestureDelay, cancellation.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_manualWheelGestureCancellation, cancellation)) return;
                CompleteManualWheelGesture();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CompleteManualWheelGesture()
    {
        var cancellation = _manualWheelGestureCancellation;
        _manualWheelGestureCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (_manualWheelGestureStart is not { } before) return;
        _manualWheelGestureStart = null;
        _manualCameraHistory.Record(before, _viewport);
    }

    private void ResetManualCameraHistory()
    {
        _manualWheelGestureCancellation?.Cancel();
        _manualWheelGestureCancellation?.Dispose();
        _manualWheelGestureCancellation = null;
        _manualWheelGestureStart = null;
        _manualCameraHistory.Reset();
    }

    private void ApplyManualCameraViewport(BoardViewport target)
    {
        CancelCameraAnimation();
        CancelProgressiveFocusLoad();
        _focusController.Reset();
        _viewport = target;
        ApplyViewportMatrix();
        RenderVisibleItems();
        QueuePersistView();
    }

    private static bool IsTextEditingFocus() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
        or PasswordBox;

    private static string FocusStatus(BoardFocusTarget target) => target.Kind switch
    {
        BoardFocusTargetKind.FullBoard => "已聚焦完整画板",
        BoardFocusTargetKind.SingleItem => "已聚焦所选图片",
        BoardFocusTargetKind.SingleNote => "已聚焦所选便签",
        _ => "已聚焦所选内容"
    };

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                TryRecalculateFocusForViewport();
                RenderVisibleItems();
            }));
    }
}
