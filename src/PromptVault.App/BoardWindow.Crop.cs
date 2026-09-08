using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    private bool _cropModeActive;
    private long? _cropItemId;
    private BoardItemRecord? _cropOriginalItem;
    private BoardCropViewport _cropViewport;
    private BoardCropViewport _cropMinimumCover;
    private Point? _cropPanStart;
    private BoardCropViewport _cropPanOrigin;
    private bool _cropChanged;
    private bool _cropCommitActive;
    private DispatcherTimer? _cropHintTimer;

    private BoardCropViewport GetDisplayCropViewport(BoardItemRecord item) =>
        _cropModeActive && _cropItemId == item.Id
            ? _cropViewport
            : BoardCropEngine.FromItem(item);

    private void BeginCropMode()
    {
        if (_referenceLocked) return;
        if (_selectedIds.Count != 1)
        {
            SetStatus("裁剪模式需要只选择一张图片");
            return;
        }
        var item = _items.Single(candidate => _selectedIds.Contains(candidate.Id));
        if (!_realized.TryGetValue(item.Id, out var element) || FindItemImage(element)?.Source is null)
        {
            SetStatus("图片尚未就绪，暂时不能进入裁剪");
            return;
        }
        _cropItemId = item.Id;
        _cropOriginalItem = item with { };
        _cropMinimumCover = BoardCropEngine.MinimumCover(
            item.NaturalWidth,
            item.NaturalHeight,
            item.Width,
            item.Height);
        _cropViewport = BoardCropEngine.FromItem(item);
        _cropChanged = false;
        _cropModeActive = true;
        ApplyImageViewport(element, item);
        UpdateSelectionOverlay();
        ShowCropHint();
        SetStatus("裁剪模式：框内滚轮缩放，左键拖动图片");
    }

    private async Task<bool> CommitCropModeAsync(string status = "裁剪已保存")
    {
        if (_cropCommitActive) return false;
        if (!_cropModeActive || _cropItemId is not { } itemId || _cropOriginalItem is null)
        {
            EndCropSessionVisuals();
            return true;
        }
        _cropCommitActive = true;
        var enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            var current = _items.SingleOrDefault(item => item.Id == itemId);
            var changed = _cropChanged;
            if (current is not null && _cropChanged)
            {
                var before = SnapshotItems().Select(item => item.Id == itemId ? _cropOriginalItem : item).ToArray();
                ReplaceItem(BoardCropEngine.Apply(current, _cropViewport));
                if (!await SaveSelectedItemsAsync()) return false;
                CommitHistorySnapshot(before);
            }
            EndCropSessionVisuals();
            RenderVisibleItems();
            SetStatus(changed ? status : "已退出裁剪模式");
            return true;
        }
        finally { IsEnabled = enabled; _cropCommitActive = false; }
    }

    private void CancelCropMode()
    {
        if (_cropCommitActive) return;
        if (_cropOriginalItem is { } original)
        {
            ReplaceItem(original);
            if (_saveError is not null)
            {
                _pendingSaves.EnqueueItems(CurrentBoardId, [ToUpdate(original)]);
                _ = FlushPendingSavesAsync("已取消裁剪");
            }
        }
        EndCropSessionVisuals();
        RenderVisibleItems();
        SetStatus("已取消裁剪");
    }

    private void EndCropSessionVisuals()
    {
        _cropHintTimer?.Stop();
        CropModeHint.Visibility = Visibility.Collapsed;
        _cropPanStart = null;
        Mouse.OverrideCursor = null;
        _cropModeActive = false;
        _cropItemId = null;
        _cropOriginalItem = null;
        _cropChanged = false;
        UpdateSelectionOverlay();
    }

    private void ShowCropHint()
    {
        CropModeHint.Visibility = Visibility.Visible;
        _cropHintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _cropHintTimer.Stop();
        _cropHintTimer.Tick -= HideCropHint;
        _cropHintTimer.Tick += HideCropHint;
        _cropHintTimer.Start();
    }

    private void HideCropHint(object? sender, EventArgs e)
    {
        _cropHintTimer?.Stop();
        CropModeHint.Visibility = Visibility.Collapsed;
    }

    private bool TryCropWheel(MouseWheelEventArgs e)
    {
        if (!_cropModeActive || _cropItemId is not { } itemId
            || !_realized.TryGetValue(itemId, out var element)) return false;
        var point = e.GetPosition(element);
        if (point.X < 0 || point.Y < 0 || point.X > element.ActualWidth || point.Y > element.ActualHeight)
            return false;
        var factor = Math.Pow(BoardCropEngine.WheelStep, e.Delta / 120d);
        _cropViewport = BoardCropEngine.ZoomAt(
            _cropViewport,
            _cropMinimumCover,
            point.X / Math.Max(1, element.ActualWidth),
            point.Y / Math.Max(1, element.ActualHeight),
            factor);
        _cropChanged = true;
        ApplyImageViewport(element, _items.Single(item => item.Id == itemId));
        return true;
    }

    private bool TryBeginCropPointerGesture(Border border, long id, MouseButtonEventArgs e)
    {
        if (!_cropModeActive || _cropItemId != id) return false;
        if (e.ClickCount > 1)
        {
            _ = CommitCropModeAsync();
            e.Handled = true;
            return true;
        }
        _cropPanStart = e.GetPosition(border);
        _cropPanOrigin = _cropViewport;
        border.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Hand;
        e.Handled = true;
        return true;
    }

    private bool TryUpdateCropPointerGesture(Border border, MouseEventArgs e)
    {
        if (!_cropModeActive || border.Tag is not BoardItemVisualTag tag || _cropItemId != tag.ItemId
            || _cropPanStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return false;
        var point = e.GetPosition(border);
        _cropViewport = BoardCropEngine.Pan(
            _cropPanOrigin,
            point.X - start.X,
            point.Y - start.Y,
            border.ActualWidth,
            border.ActualHeight);
        _cropChanged = true;
        ApplyImageViewport(border, _items.Single(item => item.Id == _cropItemId));
        e.Handled = true;
        return true;
    }

    private bool TryEndCropPointerGesture(Border border, MouseButtonEventArgs e)
    {
        if (!_cropModeActive || border.Tag is not BoardItemVisualTag tag || _cropItemId != tag.ItemId || _cropPanStart is null) return false;
        _cropPanStart = null;
        if (border.IsMouseCaptured) border.ReleaseMouseCapture();
        Mouse.OverrideCursor = Cursors.Hand;
        e.Handled = true;
        return true;
    }

    private async Task ResetCropViewportAsync()
    {
        if (_selectedIds.Count != 1) return;
        var item = _items.Single(candidate => _selectedIds.Contains(candidate.Id));
        var reset = BoardCropEngine.MinimumCover(item.NaturalWidth, item.NaturalHeight, item.Width, item.Height);
        if (_cropModeActive && _cropItemId == item.Id)
        {
            _cropViewport = reset;
            _cropMinimumCover = reset;
            _cropChanged = true;
            if (_realized.TryGetValue(item.Id, out var element)) ApplyImageViewport(element, item);
            SetStatus("已重置为居中最小覆盖");
            return;
        }
        var before = SnapshotItems();
        ReplaceItem(BoardCropEngine.Apply(item, reset));
        CommitHistorySnapshot(before);
        await SaveSelectedItemsAsync();
        RecreateSelectedVisuals();
        SetStatus("裁剪已重置");
    }
}
