using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunCommandSmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8-03 命令烟测");
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

        _ = BuildContextMenu(BoardCommandContextKind.Canvas);
        var stopwatch = Stopwatch.StartNew();
        var canvasMenu = BuildContextMenu(BoardCommandContextKind.Canvas);
        var canvasBuildMs = stopwatch.Elapsed.TotalMilliseconds;
        var canvasTitles = MenuTitles(canvasMenu);
        canvasMenu.Placement = PlacementMode.Center;
        canvasMenu.PlacementTarget = BoardViewport;
        canvasMenu.IsOpen = true;
        await WaitForLayoutAsync();
        var canvasMenuVisible = canvasMenu.IsVisible;
        canvasMenu.IsOpen = false;

        var item = _items.First(candidate => File.Exists(ResolveItemOriginalPath(candidate)));
        _selectedIds.Clear();
        _selectedIds.Add(item.Id);
        _selectedNoteIds.Clear();
        RenderVisibleItems();
        var itemMenu = BuildContextMenu(BoardCommandContextKind.Item);
        var itemTitles = MenuTitles(itemMenu);
        var originalPath = ResolveItemOriginalPath(item);
        var originalExisted = File.Exists(originalPath);
        await ExecuteBoardCommandAsync(BoardCommandId.RemoveSelection);
        var boardItemRemoved = _items.All(candidate => candidate.Id != item.Id);
        var originalPreserved = File.Exists(originalPath);
        await UndoAsync();
        var undoRestoredBoardItem = _items.Any(candidate => candidate.Id == item.Id);

        var note = _notes.First();
        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        _selectedNoteIds.Add(note.Id);
        RenderVisibleItems();
        var noteMenu = BuildContextMenu(BoardCommandContextKind.Note);
        var noteTitles = MenuTitles(noteMenu);

        var shortGesture = new BoardRightGestureClassifier(0, 0).Release(3, 3) == BoardRightGestureKind.Menu;
        var dragGesture = new BoardRightGestureClassifier(0, 0).Release(6, 0) == BoardRightGestureKind.Drag;
        var initialTopmost = Topmost;
        await ExecuteBoardCommandAsync(BoardCommandId.ToggleTopmost);
        var shortcutCommandChangedState = Topmost != initialTopmost;
        await ExecuteBoardCommandAsync(BoardCommandId.ToggleTopmost);

        var process = Process.GetCurrentProcess();
        var dpi = VisualTreeHelper.GetDpi(this);
        var passed = canvasMenuVisible
            && canvasTitles.Contains("撤销")
            && canvasTitles.Contains("画板")
            && canvasTitles.Contains("窗口")
            && itemTitles.Contains("从画板移除")
            && itemTitles.Contains("聚焦所选")
            && noteTitles.Contains("删除便签")
            && shortGesture
            && dragGesture
            && originalExisted
            && boardItemRemoved
            && originalPreserved
            && undoRestoredBoardItem
            && shortcutCommandChangedState;
        var report = new
        {
            Milestone = "M8-03-command-context-ui-smoke",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Menus = new
            {
                CanvasMenuBuildMs = canvasBuildMs,
                CanvasMenuVisible = canvasMenuVisible,
                CanvasTopLevelCount = canvasMenu.Items.Count,
                ItemTopLevelCount = itemMenu.Items.Count,
                NoteTopLevelCount = noteMenu.Items.Count,
                CanvasTitles = canvasTitles,
                ItemTitles = itemTitles,
                NoteTitles = noteTitles
            },
            Gesture = new { ShortReleaseOpenedMenu = shortGesture, SixDipDragSuppressedMenu = dragGesture },
            Commands = new
            {
                ShortcutCommandChangedTopmost = shortcutCommandChangedState,
                BoardItemRemoved = boardItemRemoved,
                OriginalFilePreserved = originalPreserved,
                UndoRestoredBoardItem = undoRestoredBoardItem
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
        SetStatus(passed ? "M8-03 隔离命令烟测通过" : "M8-03 隔离命令烟测失败");
    }

    private static string[] MenuTitles(ItemsControl root) => root.Items
        .OfType<MenuItem>()
        .Select(item => item.Header?.ToString() ?? string.Empty)
        .ToArray();
}
