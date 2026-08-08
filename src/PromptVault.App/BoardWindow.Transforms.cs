using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private IReadOnlyList<BoardItemRecord>? _transformOriginalItems;
    private HashSet<long>? _transformSelectionIds;
    private Dictionary<long, int>? _transformItemIndexes;
    private BoardResizeGesture? _resizeGesture;
    private Point _transformPointerStartScreen;
    private double _transformDeltaX;
    private double _transformDeltaY;
    private bool _transformChanged;
    private bool _imageTransformGestureActive;
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
        var single = _selectedIds.Count == 1
            ? _items.SingleOrDefault(item => _selectedIds.Contains(item.Id))
            : null;
        var overlayBounds = single is null
            ? bounds.Bounds
            : new BoardWorldRect(single.X, single.Y, single.Width, single.Height);
        var left = overlayBounds.X * _viewport.Zoom + _viewport.OffsetX;
        var top = overlayBounds.Y * _viewport.Zoom + _viewport.OffsetY;
        var width = Math.Max(1, overlayBounds.Width * _viewport.Zoom);
        var height = Math.Max(1, overlayBounds.Height * _viewport.Zoom);
        SelectionBoundsOverlay.Width = width + 28;
        SelectionBoundsOverlay.Height = height + 28;
        SelectionBoundsOverlay.Margin = new Thickness(left - 14, top - 14, 0, 0);
        SelectionBoundsOverlay.RenderTransformOrigin = new Point(0.5, 0.5);
        SelectionBoundsOverlay.RenderTransform = single is null || Math.Abs(single.Rotation) < 0.001
            ? Transform.Identity
            : new RotateTransform(single.Rotation);
        SelectionOutline.BorderBrush = _cropModeActive
            ? new SolidColorBrush(Color.FromRgb(255, 176, 76))
            : new SolidColorBrush(Color.FromArgb(204, 99, 215, 247));
        SelectionOutline.BorderThickness = new Thickness(_cropModeActive ? 2 : 1);
        var cornerVisibility = _cropModeActive ? Visibility.Collapsed : Visibility.Visible;
        TopLeftHandle.Visibility = cornerVisibility;
        TopRightHandle.Visibility = cornerVisibility;
        BottomLeftHandle.Visibility = cornerVisibility;
        BottomRightHandle.Visibility = cornerVisibility;
        SelectionBoundsOverlay.Visibility = Visibility.Visible;
    }

    private void TransformHandleDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: string handle }
            || !Enum.TryParse(handle, out BoardResizeHandle parsed)
            || _selectedIds.Count == 0) return;
        if (_cropModeActive) return;
        var bounds = BoardCameraEngine.SelectionBounds(_items, _selectedIds, [], null);
        if (!bounds.HasValue) return;
        var initialBounds = _selectedIds.Count == 1
            ? _items.Where(item => _selectedIds.Contains(item.Id))
                .Select(item => new BoardWorldRect(item.X, item.Y, item.Width, item.Height))
                .Single()
            : bounds.Bounds;
        _resizeGesture = new BoardResizeGesture(initialBounds, parsed, 0, 0);
        _transformOriginalItems = SnapshotItems();
        _transformSelectionIds = _selectedIds.ToHashSet();
        _transformItemIndexes = _items
            .Select((item, index) => (item.Id, index))
            .Where(pair => _transformSelectionIds.Contains(pair.Id))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        _transformPointerStartScreen = Mouse.GetPosition(BoardViewport);
        _transformDeltaX = 0;
        _transformDeltaY = 0;
        _transformChanged = false;
        _imageTransformGestureActive = true;
        if (sender is Thumb thumb) thumb.CaptureMouse();
        Mouse.OverrideCursor = ((Thumb)sender).Cursor;
    }

    private void TransformHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_transformOriginalItems is null
            || _transformSelectionIds is null
            || _resizeGesture is null) return;
        double rawScreenDx;
        double rawScreenDy;
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            // Aspect locking moves the Thumb itself. Always measure from the fixed pointer-down
            // position so that this visual movement cannot feed back into the next DragDelta.
            var current = Mouse.GetPosition(BoardViewport);
            rawScreenDx = current.X - _transformPointerStartScreen.X;
            rawScreenDy = current.Y - _transformPointerStartScreen.Y;
        }
        else
        {
            // Routed diagnostics have no physical pointer, so preserve an explicit test fallback.
            _transformDeltaX += e.HorizontalChange;
            _transformDeltaY += e.VerticalChange;
            rawScreenDx = _transformDeltaX;
            rawScreenDy = _transformDeltaY;
        }
        ApplyImageTransformScreenDelta(
            rawScreenDx,
            rawScreenDy,
            free: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
            fromCenter: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
    }

    private void ApplyImageTransformScreenDelta(
        double rawScreenDx,
        double rawScreenDy,
        bool free,
        bool fromCenter)
    {
        if (_transformOriginalItems is null
            || _transformSelectionIds is null
            || _transformItemIndexes is null
            || _resizeGesture is null) return;
        var rotation = _transformSelectionIds.Count == 1
            ? _transformOriginalItems.Single(item => _transformSelectionIds.Contains(item.Id)).Rotation
            : 0;
        var (localDx, localDy) = BoardTransformEngine.ScreenDeltaToLocal(
            rawScreenDx,
            rawScreenDy,
            _viewport.Zoom,
            rotation);
        var target = _resizeGesture.Calculate(
            localDx,
            localDy,
            preserveAspect: !free,
            fromCenter: fromCenter);
        foreach (var id in _transformSelectionIds)
        {
            if (!_transformItemIndexes.TryGetValue(id, out var index)
                || index < 0
                || index >= _items.Count
                || index >= _transformOriginalItems.Count) continue;
            _items[index] = BoardTransformEngine.ScaleItem(
                _transformOriginalItems[index],
                _resizeGesture.OriginalBounds,
                target);
        }
        BoardViewportEngine.RefreshBounds(_items, _transformSelectionIds);
        RestoreTransformSelection();
        foreach (var id in _transformSelectionIds)
        {
            if (!_transformItemIndexes.TryGetValue(id, out var index)
                || index < 0
                || index >= _items.Count
                || !_realized.TryGetValue(id, out var element)) continue;
            UpdateItemElement(element, _items[index]);
        }
        _transformChanged = Math.Abs(rawScreenDx) > 0.001 || Math.Abs(rawScreenDy) > 0.001;
        UpdateSelectionOverlay();
    }

    private async void TransformHandleDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Thumb thumb && thumb.IsMouseCaptured) thumb.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        if (_transformOriginalItems is not { } before)
        {
            ClearImageTransformGesture();
            return;
        }
        var changed = _transformChanged;
        RestoreTransformSelection();
        _transformOriginalItems = null;
        _resizeGesture = null;
        ClearImageTransformGesture();
        if (e.Canceled)
        {
            ReplaceItems(before);
            RenderVisibleItems();
            return;
        }
        if (!changed)
        {
            UpdateSelectionOverlay();
            return;
        }
        CommitHistorySnapshot(before);
        await SaveSelectedItemsAsync();
        RenderVisibleItems();
        SetStatus("变换已保存");
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

    private bool ExitTransformMode()
    {
        if (_rotationGestureActive)
        {
            _ = CompleteRotationGestureAsync(canceled: true);
            return true;
        }
        if (_transformOriginalItems is { } before)
        {
            RestoreTransformSelection();
            _transformOriginalItems = null;
            _resizeGesture = null;
            if (Mouse.Captured is Thumb capturedThumb) capturedThumb.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;
            ClearImageTransformGesture();
            ReplaceItems(before);
            RenderVisibleItems();
            return true;
        }
        if (_noteResizeOrigin is { } noteBefore)
        {
            _noteResizeOrigin = null;
            _noteGestureSnapshot = null;
            if (Mouse.Captured is Thumb noteThumb) noteThumb.ReleaseMouseCapture();
            ReplaceNote(noteBefore);
            RenderVisibleItems();
            return true;
        }
        if (_cropModeActive)
        {
            CancelCropMode();
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

    private void RestoreTransformSelection()
    {
        if (_transformSelectionIds is null) return;
        _selectedIds.Clear();
        _selectedIds.UnionWith(_transformSelectionIds);
        _selectedNoteId = null;
    }

    private void ClearImageTransformGesture()
    {
        _transformSelectionIds = null;
        _transformItemIndexes = null;
        _transformPointerStartScreen = default;
        _imageTransformGestureActive = false;
        _transformChanged = false;
        _transformDeltaX = 0;
        _transformDeltaY = 0;
    }
}
