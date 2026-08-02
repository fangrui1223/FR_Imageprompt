using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private IReadOnlyList<BoardItemRecord>? _transformOriginalItems;
    private BoardWorldRect _transformOriginalBounds;
    private BoardResizeHandle _transformHandle;
    private double _transformDeltaX;
    private double _transformDeltaY;
    private bool _transformChanged;
    private bool _cropModeActive;
    private bool _rotationGestureActive;
    private IReadOnlyList<BoardItemRecord>? _rotationOriginalItems;
    private BoardWorldRect _rotationBounds;
    private double _rotationStartAngle;
    private double _rotationDelta;

    private void UpdateSelectionOverlay()
    {
        if (_selectedIds.Count == 0 || _selectedNoteId is not null)
        {
            SelectionBoundsOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var bounds = BoardCameraEngine.SelectionBounds(_items, _selectedIds, [], null);
        if (!bounds.HasValue)
        {
            SelectionBoundsOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var left = bounds.Bounds.X * _viewport.Zoom + _viewport.OffsetX;
        var top = bounds.Bounds.Y * _viewport.Zoom + _viewport.OffsetY;
        var width = Math.Max(1, bounds.Bounds.Width * _viewport.Zoom);
        var height = Math.Max(1, bounds.Bounds.Height * _viewport.Zoom);
        SelectionBoundsOverlay.Width = width;
        SelectionBoundsOverlay.Height = height;
        SelectionBoundsOverlay.Margin = new Thickness(left, top, 0, 0);
        SelectionBoundsOverlay.BorderBrush = _cropModeActive
            ? new SolidColorBrush(Color.FromRgb(255, 176, 76))
            : new SolidColorBrush(Color.FromArgb(204, 99, 215, 247));
        SelectionBoundsOverlay.Visibility = Visibility.Visible;
    }

    private void TransformHandleDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: string handle }
            || !Enum.TryParse(handle, out BoardResizeHandle parsed)
            || _selectedIds.Count == 0) return;
        if (_cropModeActive && _selectedIds.Count != 1) return;
        var bounds = BoardCameraEngine.SelectionBounds(_items, _selectedIds, [], null);
        if (!bounds.HasValue) return;
        _transformHandle = parsed;
        _transformOriginalBounds = bounds.Bounds;
        _transformOriginalItems = SnapshotItems();
        _transformDeltaX = 0;
        _transformDeltaY = 0;
        _transformChanged = false;
    }

    private void TransformHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_transformOriginalItems is null) return;
        _transformDeltaX += e.HorizontalChange / Math.Max(0.1, _viewport.Zoom);
        _transformDeltaY += e.VerticalChange / Math.Max(0.1, _viewport.Zoom);
        if (_cropModeActive)
        {
            var original = _transformOriginalItems.Single(item => _selectedIds.Contains(item.Id));
            ReplaceItem(BoardTransformEngine.CropFromCorner(
                original,
                _transformHandle,
                _transformDeltaX,
                _transformDeltaY));
            _transformChanged = true;
            RecreateSelectedVisuals();
            UpdateSelectionOverlay();
            return;
        }
        var free = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var fromCenter = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var target = BoardTransformEngine.ResizeBounds(
            _transformOriginalBounds,
            _transformHandle,
            _transformDeltaX,
            _transformDeltaY,
            preserveAspect: !free,
            fromCenter: fromCenter);
        var transformed = BoardTransformEngine.ScaleSelection(
            _transformOriginalItems,
            _selectedIds,
            _transformOriginalBounds,
            target);
        ReplaceItems(transformed);
        _transformChanged = true;
        RenderVisibleItems();
    }

    private async void TransformHandleDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_transformOriginalItems is not { } before) return;
        _transformOriginalItems = null;
        if (e.Canceled)
        {
            ReplaceItems(before);
            RenderVisibleItems();
            return;
        }
        if (!_transformChanged) return;
        CommitHistorySnapshot(before);
        await SaveSelectedItemsAsync();
        RecreateSelectedVisuals();
        UpdateSelectionOverlay();
        SetStatus(_cropModeActive ? "裁剪已保存" : "变换已保存");
    }

    private void BeginRotationGesture(Point pointerWorld)
    {
        var bounds = BoardCameraEngine.SelectionBounds(_items, _selectedIds, [], null);
        if (!bounds.HasValue) return;
        _rotationBounds = bounds.Bounds;
        _rotationOriginalItems = SnapshotItems();
        _rotationStartAngle = BoardTransformEngine.AngleDegrees(
            _rotationBounds.X + _rotationBounds.Width / 2,
            _rotationBounds.Y + _rotationBounds.Height / 2,
            pointerWorld.X,
            pointerWorld.Y);
        _rotationDelta = 0;
        _rotationGestureActive = true;
    }

    private void UpdateRotationGesture(Point pointerWorld)
    {
        if (!_rotationGestureActive || _rotationOriginalItems is null) return;
        var centerX = _rotationBounds.X + _rotationBounds.Width / 2;
        var centerY = _rotationBounds.Y + _rotationBounds.Height / 2;
        var angle = BoardTransformEngine.AngleDegrees(centerX, centerY, pointerWorld.X, pointerWorld.Y);
        _rotationDelta = angle - _rotationStartAngle;
        var transformed = BoardTransformEngine.RotateSelection(
            _rotationOriginalItems,
            _selectedIds,
            centerX,
            centerY,
            _rotationDelta,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        ReplaceItems(transformed);
        RenderVisibleItems();
    }

    private async Task CompleteRotationGestureAsync(bool canceled = false)
    {
        if (!_rotationGestureActive || _rotationOriginalItems is not { } before) return;
        _rotationGestureActive = false;
        _rotationOriginalItems = null;
        if (canceled)
        {
            ReplaceItems(before);
            RenderVisibleItems();
            return;
        }
        if (Math.Abs(_rotationDelta) < 0.01) return;
        CommitHistorySnapshot(before);
        await SaveSelectedItemsAsync();
        SetStatus("旋转已保存");
    }

    private void ToggleCropMode()
    {
        if (_selectedIds.Count != 1)
        {
            SetStatus("裁剪模式需要只选择一张图片");
            return;
        }
        _cropModeActive = !_cropModeActive;
        UpdateSelectionOverlay();
        SetStatus(_cropModeActive ? "裁剪模式：拖动四角调整显示区域" : "已退出裁剪模式");
    }

    private bool ExitTransformMode()
    {
        if (_rotationGestureActive)
        {
            _ = CompleteRotationGestureAsync(canceled: true);
            return true;
        }
        if (_transformOriginalItems is { } before)
        {
            _transformOriginalItems = null;
            ReplaceItems(before);
            RenderVisibleItems();
            return true;
        }
        if (_cropModeActive)
        {
            _cropModeActive = false;
            UpdateSelectionOverlay();
            SetStatus("已退出裁剪模式");
            return true;
        }
        return false;
    }

    private void ReplaceItems(IReadOnlyList<BoardItemRecord> replacements)
    {
        _items.Clear();
        _items.AddRange(replacements);
        BoardViewportEngine.RefreshBounds(_items, _selectedIds);
    }
}
