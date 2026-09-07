using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PromptVault.App.Services;
using PromptVault.Core;
using Brush = System.Windows.Media.Brush;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Panel = System.Windows.Controls.Panel;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace PromptVault.App;

public partial class BoardWindow : Window
{
    internal const string BoardItemIdsDragFormat = "PromptVault.BoardItemIds";
    private static BoardNoteStyle NewNoteStyle { get; } = (BoardNoteStyle.Default with
    {
        FontSize = 32,
        VerticalPadding = 16
    }).Normalize();

    private readonly LibraryRepository _repository;
    private readonly BoardWorkspaceService _workspace;
    private readonly AppSettings _settings;
    private readonly List<BoardRecord> _boards = [];
    private readonly List<BoardItemRecord> _items = [];
    private readonly List<BoardNoteRecord> _notes = [];
    private readonly List<BoardGroupRecord> _groups = [];
    private readonly HashSet<long> _selectedIds = [];
    private readonly HashSet<long> _selectedNoteIds = [];
    private readonly Dictionary<long, FrameworkElement> _realized = [];
    private readonly Dictionary<long, BoardNoteVisual> _realizedNotes = [];
    private readonly Stack<BoardSceneSnapshot> _undo = [];
    private readonly Stack<BoardSceneSnapshot> _redo = [];
    private readonly BoardFocusController _focusController = new();
    private readonly BoardManualCameraHistory _manualCameraHistory = new();
    private BoardViewport _viewport = new(0, 0, 1, 0, 0);
    private Point? _panStart;
    private BoardViewport _panStartViewport;
    private long? _dragItemId;
    private Point _itemPointerStartScreen;
    private bool _itemDragActive;
    private long? _dragNoteId;
    private Point _notePointerStartScreen;
    private bool _noteDragActive;
    private BoardSceneSnapshot? _noteGestureSnapshot;
    private Point _dragStartWorld;
    private Dictionary<long, (double X, double Y)> _dragOrigins = [];
    private (double X, double Y) _noteDragOrigin;
    private BoardNoteRecord? _noteResizeOrigin;
    private BoardResizeHandle _noteResizeHandle;
    private double _noteResizeDeltaX;
    private double _noteResizeDeltaY;
    private long? _editingNoteId;
    private string? _editingNoteOriginalText;
    private long? _newUnconfirmedNoteId;
    private BoardSceneSnapshot? _noteEditSnapshot;
    private bool _cancelingNoteEdit;
    private Point? _marqueeStartScreen;
    private HashSet<long> _marqueeBaseline = [];
    private HashSet<long> _marqueeBaselineNoteIds = [];
    private bool _marqueeActive;
    private IReadOnlyList<BoardItemRecord>? _pendingHistorySnapshot;
    private bool _loadingBoard;
    private bool _persistViewQueued;
    private CancellationTokenSource? _viewSaveCancellation;
    private CancellationTokenSource? _cameraAnimationCancellation;
    private CancellationTokenSource? _manualWheelGestureCancellation;
    private BoardViewport? _manualWheelGestureStart;
    private BoardRightGestureClassifier? _rightGesture;
    private Point _rightPointerStartScreen;
    private BoardCommandContextKind _rightContext;
    private long? _rightTargetId;
    private bool _rightWindowDragStarted;
    private bool _rightCanvasPanStarted;
    private bool _rightControlPressed;
    private Point _rightPointerStartPhysical;
    private double _rightWindowStartLeft;
    private double _rightWindowStartTop;
    private ContextMenu? _openBoardContextMenu;

    private sealed record BoardSceneSnapshot(
        IReadOnlyList<BoardItemRecord> Items,
        IReadOnlyList<BoardNoteRecord> Notes);

    private long? SingleSelectedNoteId => _selectedNoteIds.Count == 1
        ? _selectedNoteIds.First()
        : null;

    internal BoardWindow(
        LibraryRepository repository,
        BoardWorkspaceService workspace,
        AppSettings settings,
        long boardId)
    {
        _repository = repository;
        _pendingSaves = new BoardPendingSaves(repository);
        _workspace = workspace;
        _settings = settings;
        CurrentBoardId = boardId;
        InitializeComponent();
        ApplyTopmostPreference(_settings.BoardAlwaysOnTop);
    }

    internal long CurrentBoardId { get; private set; }

    private async void BoardWindowLoaded(object sender, RoutedEventArgs e)
    {
        InitializeBoardChrome();
        await ReloadBoardsAsync(CurrentBoardId);
    }

    internal async Task AddCollectionItemsAsync(IReadOnlyList<BoardAddItem> requests)
    {
        if (requests.Count == 0) return;
        await EnsureBoardLoadedAsync();
        if (_pendingSaves.HasPending && !await FlushPendingSavesAsync()) return;
        PushUndoSnapshot();
        var center = BoardViewportEngine.ScreenToWorld(
            _viewport,
            Math.Max(1, BoardViewport.ActualWidth) / 2,
            Math.Max(1, BoardViewport.ActualHeight) / 2);
        var placements = new List<BoardItemPlacementInput>(requests.Count);
        var maxZ = _items.Count == 0 ? 0 : _items.Max(item => item.ZIndex) + 1;
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var aspect = request.Width > 0 && request.Height > 0
                ? request.Width / (double)request.Height
                : 1.5;
            var width = 320d;
            var height = Math.Clamp(width / Math.Clamp(aspect, 0.2, 5), 90, 480);
            var column = index % 4;
            var row = index / 4;
            placements.Add(new BoardItemPlacementInput(
                request.CollectionItemId,
                center.X - 160 + column * 344,
                center.Y - height / 2 + row * 260,
                width,
                height,
                maxZ + index));
        }
        try
        {
            var updated = await _repository.AddBoardItemsAsync(CurrentBoardId, placements);
            _items.Clear();
            _items.AddRange(updated);
            BoardViewportEngine.Invalidate(_items);
            _selectedIds.Clear();
            foreach (var item in updated.TakeLast(requests.Count)) _selectedIds.Add(item.Id);
            RenderVisibleItems();
            SetStatus($"已加入 {requests.Count} 张图片");
        }
        catch
        {
            if (_undo.Count > 0) _undo.Pop();
            UpdateUndoButtons();
            throw;
        }
    }

    private async Task EnsureBoardLoadedAsync()
    {
        if (_boards.Count == 0) await ReloadBoardsAsync(CurrentBoardId);
    }

    private async Task ReloadBoardsAsync(long boardId)
    {
        _loadingBoard = true;
        try
        {
            _boards.Clear();
            _boards.AddRange(await _repository.GetBoardsAsync());
            BoardSelector.ItemsSource = null;
            BoardSelector.ItemsSource = _boards;
            BoardSelector.SelectedItem = _boards.FirstOrDefault(board => board.Id == boardId);
            await LoadBoardAsync(boardId);
        }
        finally
        {
            _loadingBoard = false;
        }
    }

    private async Task LoadBoardAsync(long boardId)
    {
        if (_boardBoundaryActive) return;
        _boardBoundaryActive = true;
        var enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            if (_boardLoaded && !await PrepareBoardBoundaryAsync()) return;
            await LoadBoardCoreAsync(boardId);
            _boardLoaded = true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-load", "Board could not be switched.", ex);
            SetStatus($"未切换画板：{ex.Message}");
        }
        finally
        {
            var loading = _loadingBoard;
            _loadingBoard = true;
            BoardSelector.SelectedItem = _boards.FirstOrDefault(board => board.Id == CurrentBoardId);
            _loadingBoard = loading;
            IsEnabled = enabled;
            _boardBoundaryActive = false;
        }
    }

    private async Task LoadBoardCoreAsync(long boardId)
    {
        CancelCameraAnimation();
        CancelProgressiveFocusLoad();
        if (BoardInspector.Visibility == Visibility.Visible) CloseInspector();
        ExitTransformMode();
        _focusController.Reset();
        ResetManualCameraHistory();
        var document = await _repository.GetBoardDocumentAsync(boardId);
        if (document is null) return;
        var previous = CurrentBoardId;
        CurrentBoardId = boardId;
        if (previous != boardId) _workspace.BoardChanged(this, previous, boardId);
        Title = $"FR_Imageprompt · {document.Board.Name}";
        _items.Clear();
        _items.AddRange(document.Items);
        BoardViewportEngine.Invalidate(_items);
        _notes.Clear();
        _notes.AddRange(document.Notes);
        _groups.Clear();
        _groups.AddRange(document.Groups);
        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        _undo.Clear();
        _redo.Clear();
        _viewport = new BoardViewport(
            document.Board.ViewOffsetX,
            document.Board.ViewOffsetY,
            document.Board.Zoom,
            BoardViewport.ActualWidth,
            BoardViewport.ActualHeight);
        ApplyBackground(document.Board.BackgroundStyle);
        BackgroundSelector.SelectedValue = document.Board.BackgroundStyle;
        ApplyViewportMatrix();
        RenderVisibleItems();
        UpdateUndoButtons();
        SetStatus($"已恢复“{document.Board.Name}”");
    }

    private async void BoardSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingBoard || BoardSelector.SelectedItem is not BoardRecord board
            || board.Id == CurrentBoardId) return;
        await LoadBoardAsync(board.Id);
    }

    private void BoardViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewport = _viewport with { Width = e.NewSize.Width, Height = e.NewSize.Height };
        if (TryRecalculateFocusForViewport())
        {
            RenderVisibleItems();
            return;
        }
        RenderVisibleItems();
    }

    private void ApplyViewportMatrix()
    {
        CanvasMatrix.Matrix = new Matrix(
            _viewport.Zoom,
            0,
            0,
            _viewport.Zoom,
            _viewport.OffsetX,
            _viewport.OffsetY);
        ZoomText.Text = $"{_viewport.Zoom:P0}";
    }

    private void RenderVisibleItems()
    {
        if (!IsLoaded) return;
        if (BoardInspector.Visibility == Visibility.Visible
            && NotePropertiesPanel.Visibility == Visibility.Visible
            && SingleSelectedNoteId is null)
        {
            CloseInspector();
        }
        var visible = BoardViewportEngine.QueryVisible(_items, _viewport);
        var visibleIds = visible.Select(item => item.Id).ToHashSet();
        foreach (var id in _realized.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
        {
            BoardCanvas.Children.Remove(_realized[id]);
            _realized.Remove(id);
        }
        foreach (var item in visible)
        {
            if (_realized.TryGetValue(item.Id, out var element))
            {
                UpdateItemElement(element, item);
            }
            else
            {
                var created = CreateItemElement(item);
                _realized[item.Id] = created;
                BoardCanvas.Children.Add(created);
            }
        }
        var visibleNotes = BoardViewportEngine.QueryVisible(_notes, _viewport);
        var visibleNoteIds = visibleNotes.Select(note => note.Id).ToHashSet();
        foreach (var id in _realizedNotes.Keys.Where(id => !visibleNoteIds.Contains(id)).ToArray())
        {
            BoardCanvas.Children.Remove(_realizedNotes[id].Root);
            _realizedNotes.Remove(id);
        }
        foreach (var note in visibleNotes)
        {
            if (_realizedNotes.TryGetValue(note.Id, out var visual))
            {
                UpdateNoteVisual(visual, note);
            }
            else
            {
                var created = CreateNoteVisual(note);
                _realizedNotes[note.Id] = created;
                BoardCanvas.Children.Add(created.Root);
            }
        }
        EmptyBoardState.Visibility = _items.Count == 0 && _notes.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        VisibleCountText.Text = $"{visible.Count:N0} / {_items.Count:N0} 项";
        UpdateSelectionOverlay();
        UpdateInspector();
    }

    private FrameworkElement CreateItemElement(BoardItemRecord item)
    {
        var image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            Opacity = 0,
            IsHitTestVisible = false,
            Tag = "BoardImageSource"
        };
        var surface = new Border
        {
            Tag = "BoardImageSurface",
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
        var missing = new TextBlock
        {
            Text = "原图缺失\n请在检查器中重新定位",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 194, 102)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18),
            IsHitTestVisible = false
        };
        var grid = new Grid();
        grid.Children.Add(surface);
        grid.Children.Add(image);
        grid.Children.Add(missing);
        var border = new Border
        {
            Tag = new BoardItemVisualTag(item.Id),
            Background = (Brush)FindResource("ImageWellBrush"),
            BorderThickness = new Thickness(0),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            Child = grid,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Cursor = Cursors.SizeAll
        };
        border.PreviewMouseLeftButtonDown += BoardItemMouseLeftButtonDown;
        border.PreviewMouseLeftButtonUp += BoardItemMouseLeftButtonUp;
        border.MouseMove += BoardItemMouseMove;

        UpdateItemElement(border, item);
        _ = LoadItemImageAsync(border, missing, item);
        return border;
    }

    private void UpdateItemElement(FrameworkElement element, BoardItemRecord item)
    {
        element.Width = item.Width;
        element.Height = item.Height;
        Canvas.SetLeft(element, item.X);
        Canvas.SetTop(element, item.Y);
        Panel.SetZIndex(element, item.ZIndex);
        element.RenderTransform = new RotateTransform(item.Rotation);
        if (element is Border border)
        {
            UpdateItemSelectionBorder(border, item.Id);
        }
        ApplyImageViewport(element, item);
    }

    private void UpdateItemSelectionBorder(Border border, long itemId)
    {
        var showBorder = BoardSelectionVisualPolicy.ShowIndividualImageBorder(
            _selectedIds.Contains(itemId),
            _selectedIds.Count);
        if (!showBorder)
        {
            border.BorderThickness = new Thickness(0);
            border.BorderBrush = Brushes.Transparent;
            return;
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var thickness = BoardSelectionVisualPolicy.WorldThicknessForOnePhysicalPixel(
            _viewport.Zoom,
            dpiScale);
        border.BorderThickness = new Thickness(thickness);
        border.BorderBrush = (Brush)FindResource("SelectionStrokeBrush");
    }

    private void UpdateRealizedImageSelectionBorders()
    {
        foreach (var (itemId, element) in _realized)
        {
            if (element is Border border) UpdateItemSelectionBorder(border, itemId);
        }
    }

    private BoardNoteVisual CreateNoteVisual(BoardNoteRecord note)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            IsReadOnly = true,
            IsUndoEnabled = true,
            MaxLength = 4_000,
            Tag = new BoardNoteVisualTag(note.Id),
            Text = note.Text
        };
        editor.LostKeyboardFocus += NoteEditorLostKeyboardFocus;
        editor.PreviewKeyDown += NoteEditorPreviewKeyDown;

        var resizeHandles = new[]
        {
            CreateNoteResizeHandle(note.Id, BoardResizeHandle.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE)
        };

        var grid = new Grid();
        grid.Children.Add(editor);
        foreach (var resize in resizeHandles)
        {
            grid.Children.Add(resize);
            Panel.SetZIndex(resize, 5);
        }

        var root = new Border
        {
            Tag = new BoardNoteVisualTag(note.Id),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ClipToBounds = true,
            Child = grid
        };
        root.PreviewMouseLeftButtonDown += NoteRootMouseLeftButtonDown;
        root.PreviewMouseLeftButtonUp += NoteHeaderMouseLeftButtonUp;
        root.MouseMove += NoteHeaderMouseMove;
        var visual = new BoardNoteVisual(root, editor, resizeHandles);
        UpdateNoteVisual(visual, note);
        return visual;
    }

    private Thumb CreateNoteResizeHandle(
        long noteId,
        BoardResizeHandle handle,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        System.Windows.Input.Cursor cursor)
    {
        var resize = new Thumb
        {
            Width = 28,
            Height = 28,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            Cursor = cursor,
            Style = (Style)FindResource("BoardTransformHandleStyle"),
            Tag = new NoteResizeHandleTag(noteId, handle),
            Margin = new Thickness(0)
        };
        AutomationProperties.SetAutomationId(resize, $"BoardNoteResize{handle}");
        AutomationProperties.SetName(resize, $"便签{handle}缩放手柄");
        resize.DragStarted += NoteResizeStarted;
        resize.DragDelta += NoteResizeDelta;
        resize.DragCompleted += NoteResizeCompleted;
        return resize;
    }

    private void UpdateNoteVisual(BoardNoteVisual visual, BoardNoteRecord note)
    {
        var style = BoardNoteStyleCodec.Decode(note.ColorStyle);
        var selected = _selectedNoteIds.Contains(note.Id);
        var editing = _editingNoteId == note.Id;
        visual.Root.Width = note.Width;
        visual.Root.Height = note.Height;
        Canvas.SetLeft(visual.Root, note.X);
        Canvas.SetTop(visual.Root, note.Y);
        Panel.SetZIndex(visual.Root, 1_000_000 + note.ZIndex);
        visual.Root.CornerRadius = new CornerRadius(style.CornerRadius);
        visual.Root.Background = style.BackgroundEnabled
            ? BrushWithOpacity(style.BackgroundColor, style.BackgroundOpacity)
            : Brushes.Transparent;
        visual.Root.BorderBrush = selected
            ? (Brush)FindResource("SelectionStrokeBrush")
            : Brushes.Transparent;
        visual.Root.BorderThickness = selected
            ? new Thickness(BoardSelectionVisualPolicy.WorldThicknessForOnePhysicalPixel(
                _viewport.Zoom,
                VisualTreeHelper.GetDpi(this).DpiScaleX))
            : new Thickness(0);
        visual.Editor.FontSize = style.FontSize;
        visual.Editor.SetValue(TextBlock.LineHeightProperty, style.FontSize * style.LineSpacing);
        visual.Editor.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        visual.Editor.TextAlignment = style.Alignment switch
        {
            BoardNoteTextAlignment.Center => TextAlignment.Center,
            BoardNoteTextAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
        visual.Editor.Foreground = new SolidColorBrush(ParseColor(style.TextColor));
        visual.Editor.Padding = new Thickness(12, style.VerticalPadding, 12, style.VerticalPadding);
        visual.Editor.IsReadOnly = !editing;
        visual.Editor.Focusable = editing;
        visual.Editor.IsHitTestVisible = editing;
        visual.Editor.Cursor = editing ? Cursors.IBeam : Cursors.SizeAll;
        foreach (var resize in visual.ResizeHandles)
            resize.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (!editing && !visual.Editor.IsKeyboardFocusWithin && visual.Editor.Text != note.Text)
            visual.Editor.Text = note.Text;
    }

    private static Color ParseColor(string value) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(value);

    private static Brush BrushWithOpacity(string value, double opacity)
    {
        var color = ParseColor(value);
        color.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * byte.MaxValue);
        return new SolidColorBrush(color);
    }

    private async Task LoadItemImageAsync(FrameworkElement element, TextBlock missing, BoardItemRecord item)
    {
        var original = ResolveItemOriginalPath(item);
        missing.Visibility = File.Exists(original) ? Visibility.Collapsed : Visibility.Visible;
        var thumbnail = ResolveItemThumbnailPath(item);
        if (thumbnail is null || !File.Exists(thumbnail))
        {
            SetItemImageSource(element, null, item);
            return;
        }
        try
        {
            var bitmap = await ThumbnailCache.LoadAsync(
                thumbnail,
                ThumbnailSizingPolicy.LargeGalleryPixels,
                ThumbnailRequestPriority.Visible,
                CancellationToken.None);
            SetItemImageSource(element, bitmap, item);
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-thumbnail", "Board thumbnail could not be loaded.", ex);
        }
    }

    private string ResolveItemOriginalPath(BoardItemRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.SourcePathOverride)) return item.SourcePathOverride;
        if (!string.IsNullOrWhiteSpace(item.OriginalPath)) return _repository.Paths.ToAbsolute(item.OriginalPath);
        return _repository.Paths.ToAbsolute(item.SourcePathSnapshot);
    }

    private string? ResolveItemThumbnailPath(BoardItemRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.MediumThumbnailPath))
            return _repository.Paths.ToAbsolute(item.MediumThumbnailPath);
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath))
            return _repository.Paths.ToAbsolute(item.ThumbnailPath);
        var original = ResolveItemOriginalPath(item);
        return File.Exists(original) ? original : null;
    }

    private void BoardViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TryCropWheel(e))
        {
            e.Handled = true;
            return;
        }
        BeginManualWheelGesture();
        var pointer = e.GetPosition(BoardViewport);
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        _viewport = BoardViewportEngine.ZoomAt(
            _viewport,
            pointer.X,
            pointer.Y,
            _viewport.Zoom * factor);
        ApplyViewportMatrix();
        RenderVisibleItems();
        QueuePersistView();
        e.Handled = true;
    }

    private void BoardViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            InterruptCameraAnimation();
            StartPan(e.GetPosition(BoardViewport));
            e.Handled = true;
        }
    }

    private void BoardViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            EndPan();
            e.Handled = true;
        }
    }

    private async void BoardViewportMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        CommitNoteEditingBeforePointerGesture(source);
        if (!BoardInteractionEngine.ShouldBeginBlankCanvasGesture(
                FindParent<Thumb>(source) is not null,
                IsBlankCanvasSource(source))) return;
        if (_cropModeActive && !await CommitCropModeAsync()) return;
        if (BoardInspector.Visibility == Visibility.Visible) CloseInspector();
        InterruptCameraAnimation();
        var point = e.GetPosition(BoardViewport);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            StartPan(point);
            e.Handled = true;
            return;
        }
        _marqueeStartScreen = point;
        _marqueeBaseline = _selectedIds.ToHashSet();
        _marqueeBaselineNoteIds = _selectedNoteIds.ToHashSet();
        _marqueeActive = false;
        BoardViewport.CaptureMouse();
        e.Handled = true;
    }

    private void BoardViewportMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_imageTransformGestureActive) return;
        if (_panStart is not null)
        {
            EndPan();
            e.Handled = true;
            return;
        }
        if (_marqueeStartScreen is null) return;
        if (!_marqueeActive && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _selectedIds.Clear();
            _selectedNoteIds.Clear();
            RenderVisibleItems();
        }
        EndMarquee();
        e.Handled = true;
    }

    private void BoardViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_imageTransformGestureActive) return;
        if (_rightGesture is not null)
        {
            HandleRightGestureMove(e);
            return;
        }
        if (_panStart is { } start)
        {
            var current = e.GetPosition(BoardViewport);
            _viewport = _panStartViewport with
            {
                OffsetX = _panStartViewport.OffsetX + current.X - start.X,
                OffsetY = _panStartViewport.OffsetY + current.Y - start.Y
            };
            ApplyViewportMatrix();
            RenderVisibleItems();
            return;
        }
        if (_marqueeStartScreen is not { } marqueeStart || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(BoardViewport);
        if (!_marqueeActive && !BoardInteractionEngine.ExceedsDragThreshold(
                marqueeStart.X, marqueeStart.Y, pointer.X, pointer.Y)) return;
        _marqueeActive = true;
        UpdateMarquee(marqueeStart, pointer, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void StartPan(Point point)
    {
        BeginManualCameraGesture();
        _panStart = point;
        _panStartViewport = _viewport;
        BoardViewport.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Hand;
    }

    private void EndPan()
    {
        if (_panStart is null) return;
        var before = _panStartViewport;
        _panStart = null;
        BoardViewport.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        _manualCameraHistory.Record(before, _viewport);
        QueuePersistView();
    }

    private bool IsBlankCanvasSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement
                {
                    Tag: BoardItemVisualTag or BoardNoteVisualTag or NoteResizeHandleTag
                }) return false;
            source = GetInputParent(source);
        }
        return true;
    }

    private void CommitNoteEditingBeforePointerGesture(DependencyObject? source)
    {
        if (_editingNoteId is not { } noteId) return;
        if (_realizedNotes.TryGetValue(noteId, out var visual)
            && IsInputDescendantOf(source, visual.Root)) return;
        _ = CommitNoteEditingAsync(noteId);
    }

    private static bool IsInputDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor)) return true;
            source = GetInputParent(source);
        }
        return false;
    }

    private static DependencyObject? GetInputParent(DependencyObject source)
    {
        if (source is FrameworkContentElement content) return content.Parent;
        return source is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(source)
            : LogicalTreeHelper.GetParent(source);
    }

    private void UpdateMarquee(Point start, Point current, bool additive)
    {
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        MarqueeSelection.Width = Math.Abs(current.X - start.X);
        MarqueeSelection.Height = Math.Abs(current.Y - start.Y);
        MarqueeSelection.Margin = new Thickness(left, top, 0, 0);
        MarqueeSelection.Visibility = Visibility.Visible;

        var worldStart = ToWorld(start);
        var worldCurrent = ToWorld(current);
        var worldRect = new BoardWorldRect(
            worldStart.X,
            worldStart.Y,
            worldCurrent.X - worldStart.X,
            worldCurrent.Y - worldStart.Y);
        var selection = BoardInteractionEngine.SelectItemsInMarquee(
            _items,
            worldRect,
            _marqueeBaseline,
            additive);
        _selectedIds.Clear();
        _selectedIds.UnionWith(selection);
        var noteHits = BoardInteractionEngine.SelectNotesInMarquee(_notes, worldRect);
        _selectedNoteIds.Clear();
        if (additive) _selectedNoteIds.UnionWith(_marqueeBaselineNoteIds);
        _selectedNoteIds.UnionWith(noteHits);
        RenderVisibleItems();
    }

    private void EndMarquee()
    {
        _marqueeStartScreen = null;
        _marqueeActive = false;
        _marqueeBaseline.Clear();
        _marqueeBaselineNoteIds.Clear();
        MarqueeSelection.Visibility = Visibility.Collapsed;
        if (Mouse.Captured == BoardViewport) BoardViewport.ReleaseMouseCapture();
    }

    private async void BoardItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: BoardItemVisualTag { ItemId: var id } } border) return;
        if (FindParent<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        if (TryBeginCropPointerGesture(border, id, e)) return;
        if (_cropModeActive)
        {
            if (!await CommitCropModeAsync()) return;
        }
        _selectedNoteIds.Clear();
        if (e.ClickCount > 1)
        {
            _selectedIds.Clear();
            _selectedIds.Add(id);
            RenderVisibleItems();
            FocusItemFromDoubleClick(id);
            e.Handled = true;
            return;
        }
        var selection = BoardInteractionEngine.SelectItem(
            _items,
            _selectedIds,
            id,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _selectedIds.Clear();
        _selectedIds.UnionWith(selection);
        BeginItemPointerGesture(border, id, e);
    }

    private void SelectionOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Thumb>(e.OriginalSource as DependencyObject) is not null
            || _selectedIds.Count == 0
            || sender is not Border overlay) return;
        BeginItemPointerGesture(overlay, _selectedIds.First(), e);
    }

    private void BeginItemPointerGesture(UIElement captureTarget, long id, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _dragItemId = id;
            BeginRotationGesture(ToWorld(e.GetPosition(BoardViewport)));
            captureTarget.CaptureMouse();
            RenderVisibleItems();
            e.Handled = true;
            return;
        }
        _dragItemId = id;
        _itemPointerStartScreen = e.GetPosition(BoardViewport);
        _itemDragActive = false;
        _dragStartWorld = ToWorld(e.GetPosition(BoardViewport));
        _dragOrigins = _items
            .Where(item => _selectedIds.Contains(item.Id))
            .ToDictionary(item => item.Id, item => (item.X, item.Y));
        _pendingHistorySnapshot = null;
        captureTarget.CaptureMouse();
        RenderVisibleItems();
        e.Handled = true;
    }

    private void BoardItemMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Border border && TryUpdateCropPointerGesture(border, e)) return;
        HandleItemPointerMove(e);
    }

    private void SelectionOverlayMouseMove(object sender, MouseEventArgs e)
        => HandleItemPointerMove(e);

    private void HandleItemPointerMove(MouseEventArgs e)
    {
        if (_dragItemId is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (_rotationGestureActive)
        {
            UpdateRotationGesture(ToWorld(e.GetPosition(BoardViewport)));
            e.Handled = true;
            return;
        }
        var screen = e.GetPosition(BoardViewport);
        if (!_itemDragActive)
        {
            if (!BoardInteractionEngine.ExceedsDragThreshold(
                    _itemPointerStartScreen.X,
                    _itemPointerStartScreen.Y,
                    screen.X,
                    screen.Y)) return;
            _itemDragActive = true;
            _pendingHistorySnapshot = SnapshotItems();
        }
        var current = ToWorld(screen);
        var dx = current.X - _dragStartWorld.X;
        var dy = current.Y - _dragStartWorld.Y;
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (!_dragOrigins.TryGetValue(item.Id, out var origin)) continue;
            _items[index] = item with { X = origin.X + dx, Y = origin.Y + dy };
        }
        BoardViewportEngine.RefreshBounds(_items, _selectedIds);
        RenderVisibleItems();
    }

    private async void BoardItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && TryEndCropPointerGesture(border, e)) return;
        await CompleteItemPointerGestureAsync(sender as UIElement, e);
    }

    private async void SelectionOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => await CompleteItemPointerGestureAsync(sender as UIElement, e);

    private async Task CompleteItemPointerGestureAsync(UIElement? captureTarget, MouseButtonEventArgs e)
    {
        captureTarget?.ReleaseMouseCapture();
        if (_dragItemId is null) return;
        _dragItemId = null;
        if (_rotationGestureActive)
        {
            await CompleteRotationGestureAsync();
            e.Handled = true;
            return;
        }
        if (_itemDragActive && _pendingHistorySnapshot is { } before && HasLayoutChanged(before, _items))
        {
            CommitHistorySnapshot(before);
            await SaveSelectedItemsAsync();
        }
        _itemDragActive = false;
        _pendingHistorySnapshot = null;
        e.Handled = true;
    }

    private async void NoteRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: BoardNoteVisualTag { NoteId: var id } } root
            || FindParent<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        if (_cropModeActive && !await CommitCropModeAsync()) return;
        if (e.ClickCount > 1)
        {
            BeginNoteEditing(id);
            e.Handled = true;
            return;
        }
        if (_editingNoteId == id) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
        if (note is null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!_selectedNoteIds.Add(id)) _selectedNoteIds.Remove(id);
        }
        else
        {
            if (!_selectedNoteIds.Contains(id) || _selectedNoteIds.Count > 1)
            {
                _selectedNoteIds.Clear();
                _selectedNoteIds.Add(id);
            }
            _selectedIds.Clear();
        }
        if (!_selectedNoteIds.Contains(id))
        {
            RenderVisibleItems();
            e.Handled = true;
            return;
        }
        _dragNoteId = id;
        _notePointerStartScreen = e.GetPosition(BoardViewport);
        _noteDragActive = false;
        _dragStartWorld = ToWorld(e.GetPosition(BoardViewport));
        _noteDragOrigin = (note.X, note.Y);
        _noteGestureSnapshot = SnapshotScene();
        root.CaptureMouse();
        RenderVisibleItems();
        e.Handled = true;
    }

    private void NoteHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNoteId is not { } id || e.LeftButton != MouseButtonState.Pressed) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
        if (note is null) return;
        var screen = e.GetPosition(BoardViewport);
        if (!_noteDragActive)
        {
            if (!BoardInteractionEngine.ExceedsDragThreshold(
                    _notePointerStartScreen.X,
                    _notePointerStartScreen.Y,
                    screen.X,
                    screen.Y)) return;
            _noteDragActive = true;
        }
        var current = ToWorld(screen);
        var dx = current.X - _dragStartWorld.X;
        var dy = current.Y - _dragStartWorld.Y;
        if (_noteGestureSnapshot is { } snapshot)
        {
            foreach (var original in snapshot.Notes.Where(candidate => _selectedNoteIds.Contains(candidate.Id)))
                ReplaceNote(original with { X = original.X + dx, Y = original.Y + dy });
        }
        RenderVisibleItems();
    }

    private async void NoteHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border header) header.ReleaseMouseCapture();
        if (_dragNoteId is not { } id) return;
        _dragNoteId = null;
        if (_noteDragActive)
        {
            if (_noteGestureSnapshot is { } before) CommitSceneHistorySnapshot(before);
            await SaveSelectedNotesAsync("便签位置已保存");
        }
        _noteDragActive = false;
        _noteGestureSnapshot = null;
        e.Handled = true;
    }

    private void NoteResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: NoteResizeHandleTag tag }) return;
        if (!_selectedNoteIds.Contains(tag.NoteId))
        {
            _selectedNoteIds.Clear();
            _selectedNoteIds.Add(tag.NoteId);
        }
        _selectedIds.Clear();
        _noteResizeOrigin = _notes.SingleOrDefault(candidate => candidate.Id == tag.NoteId);
        _noteGestureSnapshot = SnapshotScene();
        _noteResizeHandle = tag.Handle;
        _noteResizeDeltaX = 0;
        _noteResizeDeltaY = 0;
        RenderVisibleItems();
    }

    private void NoteResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (_noteResizeOrigin is not { } origin) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == origin.Id);
        if (note is null) return;
        _noteResizeDeltaX += e.HorizontalChange / Math.Max(0.1, _viewport.Zoom);
        _noteResizeDeltaY += e.VerticalChange / Math.Max(0.1, _viewport.Zoom);
        var target = BoardTransformEngine.ResizeBounds(
            new BoardWorldRect(origin.X, origin.Y, origin.Width, origin.Height),
            _noteResizeHandle,
            _noteResizeDeltaX,
            _noteResizeDeltaY,
            preserveAspect: false,
            fromCenter: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
            minimumEdge: 32,
            minimumHeight: 24);
        if (_noteGestureSnapshot is { } snapshot)
        {
            var originalBounds = new BoardWorldRect(origin.X, origin.Y, origin.Width, origin.Height);
            foreach (var original in snapshot.Notes.Where(candidate => _selectedNoteIds.Contains(candidate.Id)))
                ReplaceNote(ScaleNote(original, originalBounds, target));
        }
        RenderVisibleItems();
    }

    private async void NoteResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_noteResizeOrigin is not { } origin) return;
        _noteResizeOrigin = null;
        if (e.Canceled)
        {
            if (_noteGestureSnapshot is { } canceled)
            {
                foreach (var original in canceled.Notes.Where(candidate => _selectedNoteIds.Contains(candidate.Id)))
                    ReplaceNote(original);
            }
            RenderVisibleItems();
            _noteGestureSnapshot = null;
            return;
        }
        var changed = _noteGestureSnapshot is { } before
            && before.Notes.Where(candidate => _selectedNoteIds.Contains(candidate.Id)).Any(original =>
            {
                var current = _notes.Single(candidate => candidate.Id == original.Id);
                return Math.Abs(current.X - original.X) > 0.001
                    || Math.Abs(current.Y - original.Y) > 0.001
                    || Math.Abs(current.Width - original.Width) > 0.001
                    || Math.Abs(current.Height - original.Height) > 0.001;
            });
        if (changed && _noteGestureSnapshot is { } committed) CommitSceneHistorySnapshot(committed);
        _noteGestureSnapshot = null;
        if (changed) await SaveSelectedNotesAsync("便签大小已保存");
    }

    private async void NoteEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: BoardNoteVisualTag { NoteId: var id } }
            || _editingNoteId != id
            || _cancelingNoteEdit) return;
        await CommitNoteEditingAsync(id);
    }

    private async Task<bool> SaveNoteAsync(BoardNoteRecord note, string status)
    {
        _pendingSaves.EnqueueNotes(note.BoardId, [ToUpdate(note)]);
        return await FlushPendingSavesAsync(status);
    }

    private async Task<bool> SaveSelectedNotesAsync(string status)
    {
        var updates = _notes
            .Where(note => _selectedNoteIds.Contains(note.Id))
            .Select(ToUpdate)
            .ToArray();
        _pendingSaves.EnqueueNotes(CurrentBoardId, updates);
        return await FlushPendingSavesAsync(status);
    }

    private async Task<bool> SaveSelectedItemsAsync()
    {
        var updates = _items.Where(item => _selectedIds.Contains(item.Id)).Select(ToUpdate).ToArray();
        _pendingSaves.EnqueueItems(CurrentBoardId, updates);
        return await FlushPendingSavesAsync($"已保存 {updates.Length} 个画板项");
    }

    private async Task MutateSelectionAsync(Func<BoardItemRecord, BoardItemRecord> transform)
    {
        if (_selectedIds.Count == 0) return;
        var before = SnapshotItems();
        for (var index = 0; index < _items.Count; index++)
        {
            if (_selectedIds.Contains(_items[index].Id)) _items[index] = transform(_items[index]);
        }
        BoardViewportEngine.RefreshBounds(_items, _selectedIds);
        CommitHistorySnapshot(before);
        RecreateSelectedVisuals();
        await SaveSelectedItemsAsync();
    }

    private async void LayerForwardClick(object sender, RoutedEventArgs e)
    {
        await ExecuteBoardCommandAsync(BoardCommandId.LayerForward);
    }

    private async void LayerBackwardClick(object sender, RoutedEventArgs e)
    {
        await ExecuteBoardCommandAsync(BoardCommandId.LayerBackward);
    }

    private async void RotateLeftClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.RotateLeft);

    private async void RotateRightClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.RotateRight);

    private async void EnterCropClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.EnterCrop);

    private async void ResetCropClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.ResetCrop);

    private async void ResetSizeClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.ResetSize);

    private async void DeleteSelectionClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.RemoveSelection);

    private async Task DeleteSelectionAsync()
    {
        if (_selectedIds.Count == 0) return;
        PushUndoSnapshot();
        var ids = _selectedIds.ToArray();
        await _repository.DeleteBoardItemsAsync(CurrentBoardId, ids);
        _items.RemoveAll(item => _selectedIds.Contains(item.Id));
        _selectedIds.Clear();
        RenderVisibleItems();
        SetStatus($"已从画板移除 {ids.Length} 项，图库原图未改动");
    }

    private void PushUndoSnapshot() => CommitSceneHistorySnapshot(SnapshotScene());

    private IReadOnlyList<BoardItemRecord> SnapshotItems() => _items.Select(item => item with { }).ToArray();
    private IReadOnlyList<BoardNoteRecord> SnapshotNotes() => _notes.Select(note => note with { }).ToArray();
    private BoardSceneSnapshot SnapshotScene() => new(SnapshotItems(), SnapshotNotes());

    private void CommitHistorySnapshot(IReadOnlyList<BoardItemRecord> snapshot) =>
        CommitSceneHistorySnapshot(new BoardSceneSnapshot(snapshot, SnapshotNotes()));

    private void CommitSceneHistorySnapshot(BoardSceneSnapshot snapshot)
    {
        _undo.Push(snapshot);
        while (_undo.Count > 50)
        {
            var retained = _undo.Take(50).Reverse().ToArray();
            _undo.Clear();
            foreach (var entry in retained) _undo.Push(entry);
        }
        _redo.Clear();
        UpdateUndoButtons();
    }

    private async void UndoClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.Undo);

    private async Task UndoAsync()
    {
        if (_undo.Count == 0) return;
        var current = SnapshotScene();
        await RestoreSnapshotAsync(_undo.Peek(), "已撤销");
        _undo.Pop();
        _redo.Push(current);
        UpdateUndoButtons();
    }

    private async void RedoClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.Redo);

    private async Task RedoAsync()
    {
        if (_redo.Count == 0) return;
        var current = SnapshotScene();
        await RestoreSnapshotAsync(_redo.Peek(), "已重做");
        _redo.Pop();
        _undo.Push(current);
        UpdateUndoButtons();
    }

    private async Task RestoreSnapshotAsync(BoardSceneSnapshot snapshot, string status)
    {
        await _repository.ReplaceBoardSceneAsync(CurrentBoardId, snapshot.Items, snapshot.Notes);
        _items.Clear();
        _items.AddRange(snapshot.Items);
        _notes.Clear();
        _notes.AddRange(snapshot.Notes);
        BoardViewportEngine.Invalidate(_items);
        _selectedIds.RemoveWhere(id => _items.All(item => item.Id != id));
        RenderVisibleItems();
        UpdateUndoButtons();
        SetStatus(status);
    }

    private void UpdateUndoButtons()
    {
        if (UndoButton is null) return;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void FitBoardClick(object sender, RoutedEventArgs e)
    {
        _ = ExecuteBoardCommandAsync(BoardCommandId.FocusAll);
    }

    private void QueuePersistView()
    {
        _viewSaveCancellation?.Cancel();
        _viewSaveCancellation?.Dispose();
        var cancellation = _viewSaveCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var boardId = CurrentBoardId;
        _persistViewQueued = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                await (await Dispatcher.InvokeAsync(() =>
                    token.IsCancellationRequested || boardId != CurrentBoardId || _closeApproved
                        ? Task.FromResult(true) : PersistViewNowAsync()));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task<bool> PersistViewNowAsync(bool force = false)
    {
        if (!force && !_persistViewQueued && _boards.Count > 0) return !_pendingSaves.HasPending;
        _persistViewQueued = false;
        var workingViewport = _focusController.WorkingViewport(_viewport);
        _pendingSaves.EnqueueView(
            CurrentBoardId,
            workingViewport.OffsetX,
            workingViewport.OffsetY,
            workingViewport.Zoom);
        return await FlushPendingSavesAsync();
    }

    private async void BackgroundSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingBoard || BackgroundSelector.SelectedValue is not string style) return;
        try
        {
            await _repository.UpdateBoardBackgroundAsync(CurrentBoardId, style);
            ApplyBackground(style);
            SetStatus("画板背景已保存");
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-background", "Board background was not saved.", ex);
            SetStatus($"背景未保存：{ex.Message}");
        }
    }

    private async void AddNoteClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.AddNote);

    private async Task AddNoteAsync()
    {
        await EnsureBoardLoadedAsync();
        var before = SnapshotScene();
        var center = BoardViewportEngine.ScreenToWorld(
            _viewport,
            Math.Max(1, BoardViewport.ActualWidth) / 2,
            Math.Max(1, BoardViewport.ActualHeight) / 2);
        var z = _notes.Count == 0 ? 0 : _notes.Max(note => note.ZIndex) + 1;
        var note = await _repository.AddBoardNoteAsync(
            CurrentBoardId,
            "在这里记录灵感…",
            center.X - 210,
            center.Y - 70,
            420,
            140,
            z,
            BoardNoteStyleCodec.Encode(NewNoteStyle));
        _notes.Add(note);
        CommitSceneHistorySnapshot(before);
        _selectedIds.Clear();
        _selectedNoteIds.Clear();
        _selectedNoteIds.Add(note.Id);
        RenderVisibleItems();
        _newUnconfirmedNoteId = note.Id;
        BeginNoteEditing(note.Id, isNew: true);
        SetStatus("已新建便签");
    }

    private async void CycleNoteColorClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.ToggleNoteBackground);

    private async Task CycleSelectedNoteColorAsync()
    {
        if (SingleSelectedNoteId is not { } id) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
        if (note is null) return;
        var before = SnapshotScene();
        var style = BoardNoteStyleCodec.Decode(note.ColorStyle);
        note = note with
        {
            ColorStyle = BoardNoteStyleCodec.Encode(style with
            {
                BackgroundEnabled = !style.BackgroundEnabled
            })
        };
        ReplaceNote(note);
        CommitSceneHistorySnapshot(before);
        RenderVisibleItems();
        await SaveNoteAsync(note, "便签颜色已保存");
    }

    private async void DeleteNoteClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.DeleteNote);

    private async Task DeleteSelectedNoteAsync()
    {
        if (_selectedNoteIds.Count == 0) return;
        var before = SnapshotScene();
        var ids = _selectedNoteIds.ToArray();
        await _repository.DeleteBoardNotesAsync(CurrentBoardId, ids);
        _notes.RemoveAll(note => _selectedNoteIds.Contains(note.Id));
        _selectedNoteIds.Clear();
        if (_editingNoteId is { } editingId && ids.Contains(editingId)) ClearNoteEditingState();
        CommitSceneHistorySnapshot(before);
        RenderVisibleItems();
        SetStatus(ids.Length > 1 ? $"已删除 {ids.Length} 个便签" : "便签已删除");
    }

    private async void NewBoardClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.NewBoard);

    private async Task CreateBoardAsync()
    {
        if (!await PrepareBoardBoundaryAsync()) return;
        var name = UniqueBoardName("新画板");
        var board = await _repository.CreateBoardAsync(name);
        await ReloadBoardsAsync(board.Id);
    }

    private async void RenameBoardClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.RenameBoard);

    private async Task RenameCurrentBoardAsync()
    {
        if (BoardSelector.SelectedItem is not BoardRecord board) return;
        var name = PromptForText("重命名画板", "画板名称", board.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (!await PrepareBoardBoundaryAsync()) return;
            await _repository.RenameBoardAsync(board.Id, name);
            await ReloadBoardsAsync(board.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteBoardClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.DeleteBoard);

    private async Task DeleteCurrentBoardAsync()
    {
        if (BoardSelector.SelectedItem is not BoardRecord board) return;
        if (MessageBox.Show(
                this,
                $"删除画板“{board.Name}”？\n\n只删除画板布局，不会删除图库图片或原图。",
                "删除画板",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (!await PrepareBoardBoundaryAsync()) return;
        await _repository.DeleteBoardAsync(board.Id);
        var remaining = await _repository.GetBoardsAsync();
        var next = remaining.FirstOrDefault() ?? await _repository.CreateBoardAsync("灵感画板");
        await ReloadBoardsAsync(next.Id);
    }

    private async void GroupSelectionClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.GroupSelection);

    private async Task GroupSelectionAsync()
    {
        if (_selectedIds.Count == 0) return;
        var group = await _repository.CreateBoardGroupAsync(CurrentBoardId, GroupNameBox.Text);
        await _repository.SetBoardItemsGroupAsync(CurrentBoardId, _selectedIds.ToArray(), group.Id);
        await RefreshDocumentAsync();
        SetStatus($"已组合为“{group.Name}”");
    }

    private async void UngroupSelectionClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.UngroupSelection);

    private async Task UngroupSelectionAsync()
    {
        if (_selectedIds.Count == 0) return;
        await _repository.SetBoardItemsGroupAsync(CurrentBoardId, _selectedIds.ToArray(), null);
        await RefreshDocumentAsync();
        SetStatus("已取消分组");
    }

    private async void RenameSelectedGroupClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.RenameGroup);

    private async Task RenameSelectedGroupAsync()
    {
        var groupIds = _items
            .Where(item => _selectedIds.Contains(item.Id) && item.GroupId is not null)
            .Select(item => item.GroupId!.Value)
            .Distinct()
            .ToArray();
        if (groupIds.Length != 1)
        {
            SetStatus("请选择同一分组中的图片");
            return;
        }
        var group = _groups.SingleOrDefault(candidate => candidate.Id == groupIds[0]);
        if (group is null) return;
        var name = PromptForText("重命名分组", "分组名称", group.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _repository.RenameBoardGroupAsync(CurrentBoardId, group.Id, name);
        await RefreshDocumentAsync();
    }

    private async Task RefreshDocumentAsync()
    {
        var document = await _repository.GetBoardDocumentAsync(CurrentBoardId);
        if (document is null) return;
        _items.Clear();
        _items.AddRange(document.Items);
        BoardViewportEngine.Invalidate(_items);
        _notes.Clear();
        _notes.AddRange(document.Notes);
        _groups.Clear();
        _groups.AddRange(document.Groups);
        RenderVisibleItems();
    }

    private async void RelinkSourceClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.RelinkSource);

    private async Task RelinkSelectedSourceAsync()
    {
        if (_selectedIds.Count != 1) return;
        var item = _items.Single(candidate => _selectedIds.Contains(candidate.Id));
        var dialog = new OpenFileDialog
        {
            Title = "重新定位画板原图",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var before = SnapshotItems();
        ReplaceItem(item with { SourcePathOverride = dialog.FileName });
        CommitHistorySnapshot(before);
        await SaveSelectedItemsAsync();
        RecreateSelectedVisuals();
        SetStatus("已保存重新定位路径");
    }

    private void RecreateSelectedVisuals()
    {
        foreach (var id in _selectedIds.ToArray())
        {
            if (!_realized.Remove(id, out var element)) continue;
            BoardCanvas.Children.Remove(element);
        }
        RenderVisibleItems();
    }

    private void BoardWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (NoteColorPopup.IsOpen && e.Key == Key.Escape)
        {
            _noteColorCancelOnClose = true;
            NoteColorPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        var textEditing = IsTextEditingFocus();
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = RetryBoardSaveAsync();
            e.Handled = true;
            return;
        }
        if (textEditing)
        {
            if (e.Key == Key.Escape)
            {
                if (_editingNoteId is not null) _ = CancelNoteEditingAsync();
                else Keyboard.Focus(BoardViewport);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (ExitTransformMode())
            {
            }
            else if (_focusController.IsActive || _focusController.Transition?.IsRestore == true)
            {
                RestoreWorkingView();
            }
            else if (_marqueeStartScreen is not null)
            {
                EndMarquee();
            }
            else if (BoardInspector.Visibility == Visibility.Visible)
            {
                CloseInspector();
            }
            else if (_selectedIds.Count > 0 || _selectedNoteIds.Count > 0)
            {
                _selectedIds.Clear();
                _selectedNoteIds.Clear();
                CloseInspectorIfSelectionChanged();
                RenderVisibleItems();
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && _cropModeActive)
        {
            _ = CommitCropModeAsync();
            e.Handled = true;
            return;
        }
        var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (effectiveKey == Key.Q && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            ShowTopBarFromKeyboard();
            e.Handled = true;
            return;
        }
        if (HandleCameraKey(e.Key, Keyboard.Modifiers, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.ToggleTopmost);
            e.Handled = true;
        }
        else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.None)
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.ShowInspector);
            e.Handled = true;
        }
        else if ((e.Key is Key.D0 or Key.NumPad0)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.ResetView);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.Copy);
            e.Handled = true;
        }
        else if (e.Key == Key.D && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _selectedNoteIds.Count > 0)
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.DuplicateNote);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SingleSelectedNoteId is not null)
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.EditNote);
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.Paste);
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.AddNote);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.Undo);
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.Redo);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _ = ExecuteBoardCommandAsync(
                _selectedNoteIds.Count > 0 ? BoardCommandId.DeleteNote : BoardCommandId.RemoveSelection);
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _selectedIds.Clear();
            _selectedNoteIds.Clear();
            foreach (var note in _notes) _selectedNoteIds.Add(note.Id);
            foreach (var item in _items) _selectedIds.Add(item.Id);
            RenderVisibleItems();
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeApproved) e.Cancel = true;
        base.OnClosing(e);
        if (!_closeApproved && !_boardBoundaryActive) _ = CloseAfterSavingAsync();
    }

    private void BoardWindowDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(BoardItemIdsDragFormat, false)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void BoardWindowDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(BoardItemIdsDragFormat, false) is not long[] ids || ids.Length == 0) return;
        await AddCollectionItemsAsync(ids.Select(id => new BoardAddItem(id, 1600, 900)).ToArray());
        e.Handled = true;
    }

    private void ApplyBackground(string style)
    {
        BoardBackground.Background = style switch
        {
            "warm" => new SolidColorBrush(Color.FromRgb(24, 19, 15)),
            "paper" => new SolidColorBrush(Color.FromRgb(35, 36, 38)),
            "blueprint" => new SolidColorBrush(Color.FromRgb(10, 24, 34)),
            _ => new SolidColorBrush(Color.FromRgb(10, 14, 20))
        };
    }

    private void UpdateInspector()
    {
        UpdateInspectorContent();
    }

    private void SetStatus(string text)
    {
        if (_pendingSaves.HasPending && _saveError is not null) text = _saveError;
        BoardStatusText.Text = text;
        ShowStatusOverlay();
    }

    private string UniqueBoardName(string prefix)
    {
        if (_boards.All(board => !string.Equals(board.Name, prefix, StringComparison.OrdinalIgnoreCase))) return prefix;
        var index = 2;
        while (_boards.Any(board => string.Equals(board.Name, $"{prefix} {index}", StringComparison.OrdinalIgnoreCase))) index++;
        return $"{prefix} {index}";
    }

    private string? PromptForText(string title, string label, string initial)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(17, 23, 32))
        };
        var box = new TextBox { Text = initial, Height = 34, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(8, 5, 8, 5) };
        var ok = new Button { Content = "确定", Width = 80, Height = 32, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = label, Foreground = Brushes.White },
                box,
                ok
            }
        };
        box.SelectAll();
        box.Focus();
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private Point ToWorld(Point point)
    {
        var world = BoardViewportEngine.ScreenToWorld(_viewport, point.X, point.Y);
        return new Point(world.X, world.Y);
    }

    private void ReplaceItem(BoardItemRecord replacement)
    {
        var index = _items.FindIndex(item => item.Id == replacement.Id);
        if (index >= 0)
        {
            _items[index] = replacement;
            BoardViewportEngine.RefreshBounds(_items, [replacement.Id]);
        }
    }

    private void ReplaceNote(BoardNoteRecord replacement)
    {
        var index = _notes.FindIndex(note => note.Id == replacement.Id);
        if (index >= 0) _notes[index] = replacement;
    }

    private static BoardItemUpdate ToUpdate(BoardItemRecord item) => new(
        item.Id,
        item.X,
        item.Y,
        item.Width,
        item.Height,
        item.ZIndex,
        item.Rotation,
        item.CropLeft,
        item.CropTop,
        item.CropRight,
        item.CropBottom,
        item.GroupId,
        item.SourcePathOverride);

    private static BoardNoteUpdate ToUpdate(BoardNoteRecord note) => new(
        note.Id,
        note.Text,
        note.X,
        note.Y,
        note.Width,
        note.Height,
        note.ZIndex,
        note.ColorStyle);

    private static bool HasLayoutChanged(
        IReadOnlyList<BoardItemRecord> before,
        IReadOnlyList<BoardItemRecord> after) =>
        before.Count != after.Count || before.Where((item, index) =>
            index >= after.Count
            || item.X != after[index].X
            || item.Y != after[index].Y
            || item.Width != after[index].Width
            || item.Height != after[index].Height).Any();

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private sealed record BoardNoteVisual(
        Border Root,
        TextBox Editor,
        IReadOnlyList<Thumb> ResizeHandles);

    private sealed record BoardItemVisualTag(long ItemId);

    private sealed record BoardNoteVisualTag(long NoteId);

    private sealed record NoteResizeHandleTag(long NoteId, BoardResizeHandle Handle);
}
