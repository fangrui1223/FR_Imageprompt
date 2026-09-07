using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataObject = System.Windows.DataObject;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PromptVault.App;

public partial class BoardWindow
{
    private static readonly IReadOnlyDictionary<BoardCommandId, BoardCommandDefinition> BoardCommands =
        new[]
        {
            Command(BoardCommandId.Undo, "撤销", "Ctrl+Z"),
            Command(BoardCommandId.Redo, "重做", "Ctrl+Y"),
            Command(BoardCommandId.FocusSelection, "聚焦所选", "Space"),
            Command(BoardCommandId.FocusAll, "显示全部", "Ctrl+Space"),
            Command(BoardCommandId.ResetView, "恢复 100%", "Ctrl+0"),
            Command(BoardCommandId.RemoveSelection, "从画板移除", "Delete", BoardCommandDanger.RemovesBoardContent),
            Command(BoardCommandId.DeleteNote, "删除便签", "Delete", BoardCommandDanger.RemovesBoardContent),
            Command(BoardCommandId.LayerFront, "移到最前"),
            Command(BoardCommandId.LayerForward, "前移一层"),
            Command(BoardCommandId.LayerBackward, "后移一层"),
            Command(BoardCommandId.LayerBack, "移到最后"),
            Command(BoardCommandId.RotateLeft, "左转 5°"),
            Command(BoardCommandId.RotateRight, "右转 5°"),
            Command(BoardCommandId.ResetRotation, "重置旋转"),
            Command(BoardCommandId.ResetSize, "重置尺寸"),
            Command(BoardCommandId.EnterCrop, "裁剪模式"),
            Command(BoardCommandId.CompleteCrop, "完成裁剪", "Enter"),
            Command(BoardCommandId.CancelCrop, "取消裁剪", "Esc"),
            Command(BoardCommandId.ResetCrop, "重置裁剪"),
            Command(BoardCommandId.GroupSelection, "组合所选"),
            Command(BoardCommandId.UngroupSelection, "取消分组"),
            Command(BoardCommandId.RenameGroup, "重命名分组"),
            Command(BoardCommandId.AddNote, "新建便签", "Ctrl+N"),
            Command(BoardCommandId.EditNote, "编辑文字", "Enter"),
            Command(BoardCommandId.FitNoteContent, "适合文字内容"),
            Command(BoardCommandId.ToggleNoteBackground, "显示/隐藏背景"),
            Command(BoardCommandId.AlignNoteLeft, "左对齐"),
            Command(BoardCommandId.AlignNoteCenter, "居中"),
            Command(BoardCommandId.AlignNoteRight, "右对齐"),
            Command(BoardCommandId.ApplyNotePreset1, "预设 1"),
            Command(BoardCommandId.ApplyNotePreset2, "预设 2"),
            Command(BoardCommandId.ApplyNotePreset3, "预设 3"),
            Command(BoardCommandId.ApplyNotePreset4, "预设 4"),
            Command(BoardCommandId.ApplyNotePreset5, "预设 5"),
            Command(BoardCommandId.DuplicateNote, "复制便签", "Ctrl+D"),
            Command(BoardCommandId.RelinkSource, "重新定位缺失原图"),
            Command(BoardCommandId.OpenOriginal, "打开原图"),
            Command(BoardCommandId.Copy, "复制", "Ctrl+C"),
            Command(BoardCommandId.Paste, "粘贴", "Ctrl+V"),
            Command(BoardCommandId.NewBoard, "新建画板"),
            Command(BoardCommandId.RenameBoard, "重命名画板"),
            Command(BoardCommandId.DeleteBoard, "删除画板", null, BoardCommandDanger.DeletesBoard),
            Command(BoardCommandId.ChangeBackground, "画板背景"),
            Command(BoardCommandId.ShowInspector, "属性", "I"),
            Command(BoardCommandId.ToggleTopmost, "保持置顶", "Ctrl+Shift+A"),
            Command(BoardCommandId.ShowTopBar, "显示/隐藏顶部栏", "Alt+Q"),
            Command(BoardCommandId.ShowSettings, "设置"),
            Command(BoardCommandId.ShowShortcuts, "快捷键说明")
        }.ToDictionary(command => command.Id);

    private BoardCommandState CurrentCommandState(BoardCommandContextKind context) => new(
        context,
        _selectedIds.Count,
        _selectedNoteIds.Count,
        _undo.Count > 0,
        _redo.Count > 0,
        Clipboard.ContainsData(BoardItemIdsDragFormat),
        _boards.Count,
        _selectedIds.Count == 1 && !File.Exists(ResolveItemOriginalPath(_items.Single(item => _selectedIds.Contains(item.Id)))));

    private async void BoardViewportMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var initialTarget = ResolveRightTarget(e.OriginalSource as DependencyObject);
        if (_cropModeActive && initialTarget.TargetId != _cropItemId)
            if (!await CommitCropModeAsync()) return;
        var point = e.GetPosition(BoardViewport);
        _rightPointerStartScreen = point;
        _rightGesture = new BoardRightGestureClassifier(point.X, point.Y);
        _rightWindowDragStarted = false;
        _rightCanvasPanStarted = false;
        _rightControlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _rightPointerStartPhysical = PointToScreen(point);
        _rightWindowStartLeft = Left;
        _rightWindowStartTop = Top;
        (_rightContext, _rightTargetId) = initialTarget;
        if (!_rightControlPressed) SelectRightTargetIfNeeded(_rightContext, _rightTargetId);
        _panStartViewport = _viewport;
        BoardViewport.CaptureMouse();
        e.Handled = true;
    }

    private void HandleRightGestureMove(MouseEventArgs e)
    {
        if (_rightGesture is null || e.RightButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(BoardViewport);
        if (_rightGesture.Move(point.X, point.Y) != BoardRightGestureKind.Drag) return;
        if (BoardInteractionEngine.ShouldPanCanvasWithRightDrag(
            _rightControlPressed,
            WindowState == WindowState.Maximized))
        {
            if (!_rightCanvasPanStarted)
            {
                BeginManualCameraGesture();
                _rightCanvasPanStarted = true;
            }
            _viewport = _panStartViewport with
            {
                OffsetX = _panStartViewport.OffsetX + point.X - _rightPointerStartScreen.X,
                OffsetY = _panStartViewport.OffsetY + point.Y - _rightPointerStartScreen.Y
            };
            ApplyViewportMatrix();
            RenderVisibleItems();
            return;
        }
        if (!BoardInteractionEngine.ShouldMoveWindowWithRightDrag(
                _rightControlPressed,
                WindowState == WindowState.Maximized)) return;
        var pointerPhysical = PointToScreen(point);
        if (!_rightWindowDragStarted)
        {
            _rightWindowDragStarted = true;
            if (WindowState == WindowState.Maximized)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var restore = RestoreBounds;
                var ratioX = Math.Clamp(point.X / Math.Max(1, ActualWidth), 0.05, 0.95);
                WindowState = WindowState.Normal;
                Width = Math.Max(MinWidth, restore.Width);
                Height = Math.Max(MinHeight, restore.Height);
                Left = pointerPhysical.X / dpi.DpiScaleX - Width * ratioX;
                Top = pointerPhysical.Y / dpi.DpiScaleY - Math.Min(36, Height * 0.08);
                _rightWindowStartLeft = Left;
                _rightWindowStartTop = Top;
                _rightPointerStartPhysical = pointerPhysical;
                return;
            }
        }
        var currentDpi = VisualTreeHelper.GetDpi(this);
        Left = _rightWindowStartLeft + (pointerPhysical.X - _rightPointerStartPhysical.X) / currentDpi.DpiScaleX;
        Top = _rightWindowStartTop + (pointerPhysical.Y - _rightPointerStartPhysical.Y) / currentDpi.DpiScaleY;
    }

    private void BoardViewportMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rightGesture is null) return;
        var point = e.GetPosition(BoardViewport);
        var result = _rightGesture.Release(point.X, point.Y);
        _rightGesture = null;
        if (Mouse.Captured == BoardViewport) BoardViewport.ReleaseMouseCapture();
        if (result == BoardRightGestureKind.Menu && !_rightControlPressed)
        {
            if (_openBoardContextMenu is not null) _openBoardContextMenu.IsOpen = false;
            var menu = BuildContextMenu(_rightContext);
            _openBoardContextMenu = menu;
            menu.Placement = PlacementMode.MousePoint;
            menu.PlacementTarget = BoardViewport;
            menu.IsOpen = true;
        }
        else if (BoardInteractionEngine.ShouldPanCanvasWithRightDrag(
                     _rightControlPressed,
                     WindowState == WindowState.Maximized))
        {
            if (_rightCanvasPanStarted) _manualCameraHistory.Record(_panStartViewport, _viewport);
            QueuePersistView();
        }
        _rightTargetId = null;
        _rightWindowDragStarted = false;
        _rightCanvasPanStarted = false;
        _rightControlPressed = false;
        e.Handled = true;
    }

    private (BoardCommandContextKind Context, long? TargetId) ResolveRightTarget(DependencyObject? source)
    {
        var tagged = FindParent<FrameworkElement>(source);
        while (tagged is not null)
        {
            if (tagged.Tag is BoardItemVisualTag itemTag)
                return (BoardCommandContextKind.Item, itemTag.ItemId);
            if (tagged.Tag is BoardNoteVisualTag noteTag)
                return (BoardCommandContextKind.Note, noteTag.NoteId);
            tagged = VisualTreeHelper.GetParent(tagged) as FrameworkElement;
        }
        return (BoardCommandContextKind.Canvas, null);
    }

    private void SelectRightTargetIfNeeded(BoardCommandContextKind context, long? id)
    {
        if (id is null) return;
        if (context == BoardCommandContextKind.Item && !_selectedIds.Contains(id.Value))
        {
            var selection = BoardInteractionEngine.SelectItem(_items, _selectedIds, id.Value, additive: false);
            _selectedIds.Clear();
            _selectedIds.UnionWith(selection);
            _selectedNoteIds.Clear();
            _rightContext = _selectedIds.Count > 1 ? BoardCommandContextKind.MultiSelection : BoardCommandContextKind.Item;
            RenderVisibleItems();
        }
        else if (context == BoardCommandContextKind.Note)
        {
            _selectedIds.Clear();
            if (!_selectedNoteIds.Contains(id.Value))
            {
                _selectedNoteIds.Clear();
                _selectedNoteIds.Add(id.Value);
            }
            RenderVisibleItems();
        }
    }

    private bool CanExecuteBoardCommand(BoardCommandId id, BoardCommandContextKind context) =>
        BoardCommandPolicy.CanExecute(id, CurrentCommandState(context));

    private async Task ExecuteBoardCommandAsync(BoardCommandId id)
    {
        if (_boardBoundaryActive || _boardCommandActive) return;
        _boardCommandActive = true;
        try
        {
            if (_pendingSaves.HasPending && !await FlushPendingSavesAsync()) return;
            await ExecuteBoardCommandCoreAsync(id);
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-command", "Board command failed; current content and history retained.", ex);
            SetStatus($"操作未完成：{ex.Message}");
        }
        finally { _boardCommandActive = false; }
    }

    private bool _boardCommandActive;

    private async Task ExecuteBoardCommandCoreAsync(BoardCommandId id)
    {
        var context = CurrentBoardCommandContext();
        if (!CanExecuteBoardCommand(id, context)) return;
        switch (id)
        {
            case BoardCommandId.Undo: await UndoAsync(); break;
            case BoardCommandId.Redo: await RedoAsync(); break;
            case BoardCommandId.FocusSelection: ToggleSelectionFocus(); break;
            case BoardCommandId.FocusAll: FocusFullBoard(); break;
            case BoardCommandId.ResetView: ResetViewToOneHundredPercent(); break;
            case BoardCommandId.RemoveSelection: await DeleteSelectionAsync(); break;
            case BoardCommandId.DeleteNote: await DeleteSelectedNoteAsync(); break;
            case BoardCommandId.LayerFront:
                if (_selectedNoteIds.Count > 0) await MoveSelectedNotesLayerAsync(1, extreme: true);
                else await MoveSelectionToFrontAsync();
                break;
            case BoardCommandId.LayerForward:
                if (_selectedNoteIds.Count > 0) await MoveSelectedNotesLayerAsync(1);
                else await MoveSelectionOneLayerAsync(1);
                break;
            case BoardCommandId.LayerBackward:
                if (_selectedNoteIds.Count > 0) await MoveSelectedNotesLayerAsync(-1);
                else await MoveSelectionOneLayerAsync(-1);
                break;
            case BoardCommandId.LayerBack:
                if (_selectedNoteIds.Count > 0) await MoveSelectedNotesLayerAsync(-1, extreme: true);
                else await MoveSelectionToBackAsync();
                break;
            case BoardCommandId.RotateLeft: await MutateSelectionAsync(item => item with { Rotation = item.Rotation - 5 }); break;
            case BoardCommandId.RotateRight: await MutateSelectionAsync(item => item with { Rotation = item.Rotation + 5 }); break;
            case BoardCommandId.ResetRotation: await MutateSelectionAsync(item => item with { Rotation = 0 }); break;
            case BoardCommandId.ResetSize: await ResetSelectedSizeAsync(); break;
            case BoardCommandId.EnterCrop: BeginCropMode(); break;
            case BoardCommandId.CompleteCrop: await CommitCropModeAsync(); break;
            case BoardCommandId.CancelCrop: CancelCropMode(); break;
            case BoardCommandId.ResetCrop: await ResetCropViewportAsync(); break;
            case BoardCommandId.GroupSelection: await GroupSelectionAsync(); break;
            case BoardCommandId.UngroupSelection: await UngroupSelectionAsync(); break;
            case BoardCommandId.RenameGroup: await RenameSelectedGroupAsync(); break;
            case BoardCommandId.AddNote: await AddNoteAsync(); break;
            case BoardCommandId.EditNote:
                if (SingleSelectedNoteId is { } editId) BeginNoteEditing(editId);
                break;
            case BoardCommandId.FitNoteContent: await FitSelectedNoteToContentAsync(); break;
            case BoardCommandId.ToggleNoteBackground: await ToggleSelectedNoteBackgroundAsync(); break;
            case BoardCommandId.AlignNoteLeft: await AlignSelectedNotesAsync(BoardNoteTextAlignment.Left); break;
            case BoardCommandId.AlignNoteCenter: await AlignSelectedNotesAsync(BoardNoteTextAlignment.Center); break;
            case BoardCommandId.AlignNoteRight: await AlignSelectedNotesAsync(BoardNoteTextAlignment.Right); break;
            case BoardCommandId.ApplyNotePreset1: await ApplyNotePresetAsync(0); break;
            case BoardCommandId.ApplyNotePreset2: await ApplyNotePresetAsync(1); break;
            case BoardCommandId.ApplyNotePreset3: await ApplyNotePresetAsync(2); break;
            case BoardCommandId.ApplyNotePreset4: await ApplyNotePresetAsync(3); break;
            case BoardCommandId.ApplyNotePreset5: await ApplyNotePresetAsync(4); break;
            case BoardCommandId.DuplicateNote: await DuplicateSelectedNotesAsync(); break;
            case BoardCommandId.RelinkSource: await RelinkSelectedSourceAsync(); break;
            case BoardCommandId.OpenOriginal: OpenSelectedOriginal(); break;
            case BoardCommandId.Copy: CopySelection(); break;
            case BoardCommandId.Paste: await PasteManagedSelectionAsync(); break;
            case BoardCommandId.NewBoard: await CreateBoardAsync(); break;
            case BoardCommandId.RenameBoard: await RenameCurrentBoardAsync(); break;
            case BoardCommandId.DeleteBoard: await DeleteCurrentBoardAsync(); break;
            case BoardCommandId.ShowInspector: ToggleInspector(); break;
            case BoardCommandId.ToggleTopmost:
                ToggleTopmostPreference();
                break;
            case BoardCommandId.ShowShortcuts:
                MessageBox.Show(
                    this,
                    "Space 聚焦/恢复 · Ctrl+Space 显示全部 · 四角等比缩放 · Shift+拖角自由拉伸 · Alt+拖角中心缩放 · Ctrl+右键移动窗口 · 中键/Alt+左键平移画布 · I 属性",
                    "画板快捷键");
                break;
            case BoardCommandId.ShowSettings:
                SetStatus("画板设置可从顶部栏和画布菜单访问");
                break;
            case BoardCommandId.ShowTopBar:
                SetStatus("顶部栏将在沉浸窗口中显示");
                break;
        }
        RefreshOpenContextMenuState();
    }

    private async Task MoveSelectionToFrontAsync()
    {
        var top = _items.Count == 0 ? 0 : _items.Max(item => item.ZIndex);
        var next = top;
        await MutateSelectionAsync(item => item with { ZIndex = ++next });
    }

    private async Task MoveSelectionToBackAsync()
    {
        var bottom = _items.Count == 0 ? 0 : _items.Min(item => item.ZIndex);
        var next = bottom;
        await MutateSelectionAsync(item => item with { ZIndex = --next });
    }

    private async Task MoveSelectionOneLayerAsync(int direction)
    {
        await MutateSelectionAsync(item => item with { ZIndex = item.ZIndex + Math.Sign(direction) });
    }

    private async Task ResetSelectedSizeAsync()
    {
        if (_selectedNoteIds.Count > 0)
        {
            var noteBefore = SnapshotScene();
            foreach (var note in noteBefore.Notes.Where(candidate => _selectedNoteIds.Contains(candidate.Id)))
            {
                var centerX = note.X + note.Width / 2;
                var centerY = note.Y + note.Height / 2;
                ReplaceNote(note with
                {
                    X = centerX - 150,
                    Y = centerY - 110,
                    Width = 300,
                    Height = 220
                });
            }
            RenderVisibleItems();
            CommitSceneHistorySnapshot(noteBefore);
            await SaveSelectedNotesAsync("便签尺寸已重置");
            return;
        }

        if (_selectedIds.Count == 0) return;
        var before = SnapshotItems();
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (!_selectedIds.Contains(item.Id)) continue;
            _items[index] = BoardTransformEngine.ResetSize(item);
        }
        BoardViewportEngine.RefreshBounds(_items, _selectedIds);
        CommitHistorySnapshot(before);
        RecreateSelectedVisuals();
        await SaveSelectedItemsAsync();
        SetStatus(_selectedIds.Count > 1 ? "所选图片尺寸已重置" : "图片尺寸已重置");
    }

    private void OpenSelectedOriginal()
    {
        if (_selectedIds.Count != 1) return;
        var item = _items.Single(candidate => _selectedIds.Contains(candidate.Id));
        var path = ResolveItemOriginalPath(item);
        if (!File.Exists(path)) { SetStatus("原图缺失，请先重新定位"); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CopySelection()
    {
        var files = _items
            .Where(item => _selectedIds.Contains(item.Id))
            .Select(ResolveItemOriginalPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return;
        var data = new DataObject();
        var collection = new StringCollection();
        collection.AddRange(files);
        data.SetFileDropList(collection);
        Clipboard.SetDataObject(data, copy: true);
        SetStatus($"已复制 {files.Length} 个原图文件");
    }

    private async Task PasteManagedSelectionAsync()
    {
        if (Clipboard.GetData(BoardItemIdsDragFormat) is not long[] collectionItemIds || collectionItemIds.Length == 0)
        {
            SetStatus("只接受来自 FR_Imageprompt 图库的受管图片引用");
            return;
        }
        await AddCollectionItemsAsync(collectionItemIds.Select(id => new BoardAddItem(id, 1600, 900)).ToArray());
    }

    private BoardCommandContextKind CurrentBoardCommandContext() =>
        _selectedNoteIds.Count > 0 ? BoardCommandContextKind.Note
        : _selectedIds.Count > 1 ? BoardCommandContextKind.MultiSelection
        : _selectedIds.Count == 1 ? BoardCommandContextKind.Item
        : BoardCommandContextKind.Canvas;

    private ContextMenu BuildContextMenu(BoardCommandContextKind context)
    {
        var menu = new ContextMenu { Tag = context };
        if (_cropModeActive)
        {
            AddCommands(menu, BoardCommandContextKind.Item,
                BoardCommandId.CompleteCrop,
                BoardCommandId.CancelCrop,
                BoardCommandId.ResetCrop);
            menu.Closed += (_, _) => { if (ReferenceEquals(_openBoardContextMenu, menu)) _openBoardContextMenu = null; };
            return menu;
        }
        if (context == BoardCommandContextKind.Canvas)
        {
            AddCommands(menu, context, BoardCommandId.Undo, BoardCommandId.Redo);
            menu.Items.Add(new Separator());
            AddCommands(menu, context, BoardCommandId.Paste, BoardCommandId.AddNote, BoardCommandId.FocusAll, BoardCommandId.ResetView);
            menu.Items.Add(new Separator());
            menu.Items.Add(Submenu("画板", context, BoardCommandId.NewBoard, BoardCommandId.RenameBoard, BoardCommandId.DeleteBoard));
            menu.Items.Add(Submenu("窗口", context, BoardCommandId.ToggleTopmost, BoardCommandId.ShowTopBar, BoardCommandId.ShowInspector));
            menu.Items.Add(new Separator());
            AddCommands(menu, context, BoardCommandId.ShowSettings, BoardCommandId.ShowShortcuts);
        }
        else if (context == BoardCommandContextKind.Note)
        {
            AddCommands(menu, context, BoardCommandId.EditNote, BoardCommandId.FitNoteContent, BoardCommandId.ToggleNoteBackground);
            menu.Items.Add(Submenu("对齐", context, BoardCommandId.AlignNoteLeft, BoardCommandId.AlignNoteCenter, BoardCommandId.AlignNoteRight));
            menu.Items.Add(Submenu("应用预设", context, BoardCommandId.ApplyNotePreset1, BoardCommandId.ApplyNotePreset2, BoardCommandId.ApplyNotePreset3, BoardCommandId.ApplyNotePreset4, BoardCommandId.ApplyNotePreset5));
            menu.Items.Add(Submenu("层级", context, BoardCommandId.LayerFront, BoardCommandId.LayerForward, BoardCommandId.LayerBackward, BoardCommandId.LayerBack));
            menu.Items.Add(new Separator());
            AddCommands(menu, context, BoardCommandId.DuplicateNote, BoardCommandId.ShowInspector, BoardCommandId.DeleteNote);
        }
        else
        {
            AddCommands(menu, context, BoardCommandId.FocusSelection, BoardCommandId.OpenOriginal, BoardCommandId.Copy);
            menu.Items.Add(Submenu("变换", context, BoardCommandId.ResetSize, BoardCommandId.RotateLeft, BoardCommandId.RotateRight, BoardCommandId.ResetRotation));
            menu.Items.Add(Submenu("裁剪", context, BoardCommandId.EnterCrop, BoardCommandId.ResetCrop));
            menu.Items.Add(Submenu("层级", context, BoardCommandId.LayerFront, BoardCommandId.LayerForward, BoardCommandId.LayerBackward, BoardCommandId.LayerBack));
            menu.Items.Add(Submenu("分组", context, BoardCommandId.GroupSelection, BoardCommandId.UngroupSelection, BoardCommandId.RenameGroup));
            menu.Items.Add(new Separator());
            AddCommands(menu, context, BoardCommandId.RelinkSource, BoardCommandId.ShowInspector, BoardCommandId.RemoveSelection);
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(_openBoardContextMenu, menu)) _openBoardContextMenu = null; };
        return menu;
    }

    private void AddCommands(ItemsControl menu, BoardCommandContextKind context, params BoardCommandId[] ids)
    {
        foreach (var id in ids) menu.Items.Add(CreateMenuItem(id, context));
    }

    private MenuItem Submenu(string title, BoardCommandContextKind context, params BoardCommandId[] ids)
    {
        var submenu = new MenuItem { Header = title };
        AddCommands(submenu, context, ids);
        return submenu;
    }

    private MenuItem CreateMenuItem(BoardCommandId id, BoardCommandContextKind context)
    {
        var definition = BoardCommands[id];
        var item = new MenuItem
        {
            Header = definition.Title,
            InputGestureText = definition.Shortcut ?? string.Empty,
            Tag = id,
            IsEnabled = CanExecuteBoardCommand(id, context),
            IsCheckable = id == BoardCommandId.ToggleTopmost,
            IsChecked = id == BoardCommandId.ToggleTopmost && Topmost
        };
        item.Click += async (_, _) => await ExecuteBoardCommandAsync(id);
        return item;
    }

    private void RefreshOpenContextMenuState()
    {
        if (_openBoardContextMenu?.Tag is not BoardCommandContextKind context) return;
        foreach (var item in DescendantMenuItems(_openBoardContextMenu))
        {
            if (item.Tag is not BoardCommandId id) continue;
            item.IsEnabled = CanExecuteBoardCommand(id, context);
            if (id == BoardCommandId.ToggleTopmost) item.IsChecked = Topmost;
        }
    }

    private static IEnumerable<MenuItem> DescendantMenuItems(ItemsControl root)
    {
        foreach (var entry in root.Items.OfType<MenuItem>())
        {
            yield return entry;
            foreach (var child in DescendantMenuItems(entry)) yield return child;
        }
    }

    private static BoardCommandDefinition Command(
        BoardCommandId id,
        string title,
        string? shortcut = null,
        BoardCommandDanger danger = BoardCommandDanger.None) => new(id, title, shortcut, danger);
}
