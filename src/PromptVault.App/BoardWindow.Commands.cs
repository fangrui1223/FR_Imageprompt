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
            Command(BoardCommandId.EnterCrop, "裁剪模式"),
            Command(BoardCommandId.CropHorizontal, "横向收紧"),
            Command(BoardCommandId.CropVertical, "纵向收紧"),
            Command(BoardCommandId.ResetCrop, "重置裁剪"),
            Command(BoardCommandId.GroupSelection, "组合所选"),
            Command(BoardCommandId.UngroupSelection, "取消分组"),
            Command(BoardCommandId.RenameGroup, "重命名分组"),
            Command(BoardCommandId.AddNote, "新建便签", "Ctrl+N"),
            Command(BoardCommandId.CycleNoteColor, "切换便签颜色"),
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
            Command(BoardCommandId.ShowTopBar, "显示顶部栏", "Alt / F10"),
            Command(BoardCommandId.ShowSettings, "设置"),
            Command(BoardCommandId.ShowShortcuts, "快捷键说明")
        }.ToDictionary(command => command.Id);

    private BoardCommandState CurrentCommandState(BoardCommandContextKind context) => new(
        context,
        _selectedIds.Count,
        _selectedNoteId is not null,
        _undo.Count > 0,
        _redo.Count > 0,
        Clipboard.ContainsData(BoardItemIdsDragFormat),
        _boards.Count,
        _selectedIds.Count == 1 && !File.Exists(ResolveItemOriginalPath(_items.Single(item => _selectedIds.Contains(item.Id)))));

    private void BoardViewportMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(BoardViewport);
        _rightPointerStartScreen = point;
        _rightGesture = new BoardRightGestureClassifier(point.X, point.Y);
        _rightWindowDragStarted = false;
        (_rightContext, _rightTargetId) = ResolveRightTarget(e.OriginalSource as DependencyObject);
        SelectRightTargetIfNeeded(_rightContext, _rightTargetId);
        _panStartViewport = _viewport;
        BoardViewport.CaptureMouse();
        e.Handled = true;
    }

    private void HandleRightGestureMove(MouseEventArgs e)
    {
        if (_rightGesture is null || e.RightButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(BoardViewport);
        if (_rightGesture.Move(point.X, point.Y) != BoardRightGestureKind.Drag) return;
        if (WindowState == WindowState.Maximized)
        {
            InterruptCameraAnimation();
            _viewport = _panStartViewport with
            {
                OffsetX = _panStartViewport.OffsetX + point.X - _rightPointerStartScreen.X,
                OffsetY = _panStartViewport.OffsetY + point.Y - _rightPointerStartScreen.Y
            };
            ApplyViewportMatrix();
            RenderVisibleItems();
            return;
        }
        if (_rightWindowDragStarted) return;
        _rightWindowDragStarted = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void BoardViewportMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rightGesture is null) return;
        var point = e.GetPosition(BoardViewport);
        var result = _rightGesture.Release(point.X, point.Y);
        _rightGesture = null;
        if (Mouse.Captured == BoardViewport) BoardViewport.ReleaseMouseCapture();
        if (result == BoardRightGestureKind.Menu)
        {
            if (_openBoardContextMenu is not null) _openBoardContextMenu.IsOpen = false;
            var menu = BuildContextMenu(_rightContext);
            _openBoardContextMenu = menu;
            menu.Placement = PlacementMode.MousePoint;
            menu.PlacementTarget = BoardViewport;
            menu.IsOpen = true;
        }
        else if (WindowState == WindowState.Maximized)
        {
            QueuePersistView();
        }
        _rightTargetId = null;
        _rightWindowDragStarted = false;
        e.Handled = true;
    }

    private (BoardCommandContextKind Context, long? TargetId) ResolveRightTarget(DependencyObject? source)
    {
        var tagged = FindParent<FrameworkElement>(source);
        while (tagged is not null)
        {
            if (tagged.Tag is long id)
            {
                if (_realized.ContainsKey(id)) return (BoardCommandContextKind.Item, id);
                if (_realizedNotes.ContainsKey(id)) return (BoardCommandContextKind.Note, id);
            }
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
            _selectedNoteId = null;
            _rightContext = _selectedIds.Count > 1 ? BoardCommandContextKind.MultiSelection : BoardCommandContextKind.Item;
            RenderVisibleItems();
        }
        else if (context == BoardCommandContextKind.Note)
        {
            _selectedIds.Clear();
            _selectedNoteId = id;
            RenderVisibleItems();
        }
    }

    private bool CanExecuteBoardCommand(BoardCommandId id, BoardCommandContextKind context) =>
        BoardCommandPolicy.CanExecute(id, CurrentCommandState(context));

    private async Task ExecuteBoardCommandAsync(BoardCommandId id)
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
            case BoardCommandId.LayerFront: await MoveSelectionToFrontAsync(); break;
            case BoardCommandId.LayerForward: await MoveSelectionOneLayerAsync(1); break;
            case BoardCommandId.LayerBackward: await MoveSelectionOneLayerAsync(-1); break;
            case BoardCommandId.LayerBack: await MoveSelectionToBackAsync(); break;
            case BoardCommandId.RotateLeft: await MutateSelectionAsync(item => item with { Rotation = item.Rotation - 5 }); break;
            case BoardCommandId.RotateRight: await MutateSelectionAsync(item => item with { Rotation = item.Rotation + 5 }); break;
            case BoardCommandId.ResetRotation: await MutateSelectionAsync(item => item with { Rotation = 0 }); break;
            case BoardCommandId.EnterCrop: ToggleCropMode(); break;
            case BoardCommandId.CropHorizontal: await MutateSelectionAsync(TightenHorizontalCrop); break;
            case BoardCommandId.CropVertical: await MutateSelectionAsync(TightenVerticalCrop); break;
            case BoardCommandId.ResetCrop: await MutateSelectionAsync(ResetCrop); break;
            case BoardCommandId.GroupSelection: await GroupSelectionAsync(); break;
            case BoardCommandId.UngroupSelection: await UngroupSelectionAsync(); break;
            case BoardCommandId.RenameGroup: await RenameSelectedGroupAsync(); break;
            case BoardCommandId.AddNote: await AddNoteAsync(); break;
            case BoardCommandId.CycleNoteColor: await CycleSelectedNoteColorAsync(); break;
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
                MessageBox.Show(this, "Space 聚焦/恢复 · Ctrl+Space 显示全部 · Shift 多选 · 中键/Alt+左键平移 · I 属性", "画板快捷键");
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

    private void ResetViewToOneHundredPercent()
    {
        InterruptCameraAnimation();
        _viewport = BoardViewportEngine.ZoomAt(
            CurrentViewportSize(),
            Math.Max(1, BoardViewport.ActualWidth) / 2,
            Math.Max(1, BoardViewport.ActualHeight) / 2,
            1);
        ApplyViewportMatrix();
        RenderVisibleItems();
        QueuePersistView();
        SetStatus("已恢复 100% 缩放");
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

    private static BoardItemRecord TightenHorizontalCrop(BoardItemRecord item)
    {
        var increment = item.CropLeft + item.CropRight >= 0.8 ? 0 : 0.025;
        return item with { CropLeft = item.CropLeft + increment, CropRight = item.CropRight + increment };
    }

    private static BoardItemRecord TightenVerticalCrop(BoardItemRecord item)
    {
        var increment = item.CropTop + item.CropBottom >= 0.8 ? 0 : 0.025;
        return item with { CropTop = item.CropTop + increment, CropBottom = item.CropBottom + increment };
    }

    private static BoardItemRecord ResetCrop(BoardItemRecord item) => item with
    {
        CropLeft = 0,
        CropTop = 0,
        CropRight = 0,
        CropBottom = 0
    };

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
        _selectedNoteId is not null ? BoardCommandContextKind.Note
        : _selectedIds.Count > 1 ? BoardCommandContextKind.MultiSelection
        : _selectedIds.Count == 1 ? BoardCommandContextKind.Item
        : BoardCommandContextKind.Canvas;

    private ContextMenu BuildContextMenu(BoardCommandContextKind context)
    {
        var menu = new ContextMenu { Tag = context };
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
            AddCommands(menu, context, BoardCommandId.ShowInspector, BoardCommandId.CycleNoteColor, BoardCommandId.DeleteNote);
        }
        else
        {
            AddCommands(menu, context, BoardCommandId.FocusSelection, BoardCommandId.OpenOriginal, BoardCommandId.Copy);
            menu.Items.Add(Submenu("变换", context, BoardCommandId.RotateLeft, BoardCommandId.RotateRight, BoardCommandId.ResetRotation));
            menu.Items.Add(Submenu("裁剪", context, BoardCommandId.EnterCrop, BoardCommandId.CropHorizontal, BoardCommandId.CropVertical, BoardCommandId.ResetCrop));
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
