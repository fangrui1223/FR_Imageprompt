using System.Windows;
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

    private readonly LibraryRepository _repository;
    private readonly BoardWorkspaceService _workspace;
    private readonly AppSettings _settings;
    private readonly List<BoardRecord> _boards = [];
    private readonly List<BoardItemRecord> _items = [];
    private readonly List<BoardNoteRecord> _notes = [];
    private readonly List<BoardGroupRecord> _groups = [];
    private readonly HashSet<long> _selectedIds = [];
    private readonly Dictionary<long, FrameworkElement> _realized = [];
    private readonly Dictionary<long, BoardNoteVisual> _realizedNotes = [];
    private readonly Stack<IReadOnlyList<BoardItemRecord>> _undo = [];
    private readonly Stack<IReadOnlyList<BoardItemRecord>> _redo = [];
    private readonly BoardFocusController _focusController = new();
    private BoardViewport _viewport = new(0, 0, 1, 0, 0);
    private Point? _panStart;
    private BoardViewport _panStartViewport;
    private long? _dragItemId;
    private Point _itemPointerStartScreen;
    private bool _itemDragActive;
    private long? _dragNoteId;
    private Point _notePointerStartScreen;
    private bool _noteDragActive;
    private Point _dragStartWorld;
    private Dictionary<long, (double X, double Y)> _dragOrigins = [];
    private (double X, double Y) _noteDragOrigin;
    private BoardNoteRecord? _noteResizeOrigin;
    private long? _selectedNoteId;
    private Point? _marqueeStartScreen;
    private HashSet<long> _marqueeBaseline = [];
    private long? _marqueeBaselineNoteId;
    private bool _marqueeActive;
    private IReadOnlyList<BoardItemRecord>? _pendingHistorySnapshot;
    private bool _loadingBoard;
    private bool _persistViewQueued;
    private CancellationTokenSource? _viewSaveCancellation;
    private CancellationTokenSource? _cameraAnimationCancellation;
    private BoardRightGestureClassifier? _rightGesture;
    private Point _rightPointerStartScreen;
    private BoardCommandContextKind _rightContext;
    private long? _rightTargetId;
    private bool _rightWindowDragStarted;
    private ContextMenu? _openBoardContextMenu;

    internal BoardWindow(
        LibraryRepository repository,
        BoardWorkspaceService workspace,
        AppSettings settings,
        long boardId)
    {
        _repository = repository;
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
        CancelCameraAnimation();
        CancelProgressiveFocusLoad();
        ExitTransformMode();
        _focusController.Reset();
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
        _selectedNoteId = null;
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
        await PersistViewNowAsync(force: true);
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
            Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = true
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
        grid.Children.Add(image);
        grid.Children.Add(missing);
        var border = new Border
        {
            Tag = item.Id,
            Background = (Brush)FindResource("ImageWellBrush"),
            BorderThickness = new Thickness(2),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = grid,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Cursor = Cursors.SizeAll
        };
        border.PreviewMouseLeftButtonDown += BoardItemMouseLeftButtonDown;
        border.PreviewMouseLeftButtonUp += BoardItemMouseLeftButtonUp;
        border.MouseMove += BoardItemMouseMove;

        UpdateItemElement(border, item);
        _ = LoadItemImageAsync(image, missing, item);
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
            border.BorderBrush = _selectedIds.Contains(item.Id)
                ? (Brush)FindResource("SelectionStrokeBrush")
                : (Brush)FindResource("CardBorderBrush");
        }
    }

    private BoardNoteVisual CreateNoteVisual(BoardNoteRecord note)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(13, 10, 13, 13),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(35, 31, 24)),
            FontSize = 15,
            Tag = note.Id,
            Text = note.Text
        };
        editor.LostKeyboardFocus += NoteEditorLostKeyboardFocus;

        var header = new Border
        {
            Height = 28,
            Cursor = Cursors.SizeAll,
            Tag = note.Id,
            Child = new TextBlock
            {
                Text = "便签",
                Margin = new Thickness(10, 5, 0, 0),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 35, 31, 24))
            }
        };
        header.PreviewMouseLeftButtonDown += NoteHeaderMouseLeftButtonDown;
        header.PreviewMouseLeftButtonUp += NoteHeaderMouseLeftButtonUp;
        header.MouseMove += NoteHeaderMouseMove;

        var resize = new Thumb
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Background = new SolidColorBrush(Color.FromArgb(165, 50, 45, 37)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Tag = note.Id,
            Margin = new Thickness(0, 0, -4, -4)
        };
        resize.DragStarted += NoteResizeStarted;
        resize.DragDelta += NoteResizeDelta;
        resize.DragCompleted += NoteResizeCompleted;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(editor, 1);
        Grid.SetRowSpan(resize, 2);
        grid.Children.Add(header);
        grid.Children.Add(editor);
        grid.Children.Add(resize);
        Panel.SetZIndex(resize, 5);

        var root = new Border
        {
            Tag = note.Id,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(2),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 4,
                Opacity = 0.28
            },
            Child = grid
        };
        root.PreviewMouseLeftButtonDown += NoteRootMouseLeftButtonDown;
        var visual = new BoardNoteVisual(root, editor, header);
        UpdateNoteVisual(visual, note);
        return visual;
    }

    private void UpdateNoteVisual(BoardNoteVisual visual, BoardNoteRecord note)
    {
        visual.Root.Width = note.Width;
        visual.Root.Height = note.Height;
        Canvas.SetLeft(visual.Root, note.X);
        Canvas.SetTop(visual.Root, note.Y);
        Panel.SetZIndex(visual.Root, 1_000_000 + note.ZIndex);
        visual.Root.Background = NoteBrush(note.ColorStyle);
        visual.Header.Background = NoteHeaderBrush(note.ColorStyle);
        visual.Root.BorderBrush = _selectedNoteId == note.Id
            ? (Brush)FindResource("SelectionStrokeBrush")
            : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        if (!visual.Editor.IsKeyboardFocusWithin && visual.Editor.Text != note.Text)
            visual.Editor.Text = note.Text;
    }

    private static Brush NoteBrush(string style) => new SolidColorBrush(style switch
    {
        "rose" => Color.FromRgb(247, 196, 205),
        "blue" => Color.FromRgb(183, 218, 235),
        "slate" => Color.FromRgb(200, 207, 213),
        _ => Color.FromRgb(245, 224, 153)
    });

    private static Brush NoteHeaderBrush(string style) => new SolidColorBrush(style switch
    {
        "rose" => Color.FromRgb(232, 154, 171),
        "blue" => Color.FromRgb(123, 181, 210),
        "slate" => Color.FromRgb(147, 158, 168),
        _ => Color.FromRgb(224, 190, 83)
    });

    private async Task LoadItemImageAsync(System.Windows.Controls.Image image, TextBlock missing, BoardItemRecord item)
    {
        var original = ResolveItemOriginalPath(item);
        missing.Visibility = File.Exists(original) ? Visibility.Collapsed : Visibility.Visible;
        var thumbnail = ResolveItemThumbnailPath(item);
        if (thumbnail is null || !File.Exists(thumbnail))
        {
            image.Source = null;
            return;
        }
        try
        {
            var bitmap = await ThumbnailCache.LoadAsync(
                thumbnail,
                ThumbnailSizingPolicy.LargeGalleryPixels,
                ThumbnailRequestPriority.Visible,
                CancellationToken.None);
            image.Source = ApplyCrop(bitmap, item);
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
        InterruptCameraAnimation();
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

    private void BoardViewportMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsBlankCanvasSource(e.OriginalSource as DependencyObject)) return;
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
        _marqueeBaselineNoteId = _selectedNoteId;
        _marqueeActive = false;
        BoardViewport.CaptureMouse();
        e.Handled = true;
    }

    private void BoardViewportMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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
            _selectedNoteId = null;
            RenderVisibleItems();
        }
        EndMarquee();
        e.Handled = true;
    }

    private void BoardViewportMouseMove(object sender, MouseEventArgs e)
    {
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
        _panStart = point;
        _panStartViewport = _viewport;
        BoardViewport.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Hand;
    }

    private void EndPan()
    {
        if (_panStart is null) return;
        _panStart = null;
        BoardViewport.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        QueuePersistView();
    }

    private bool IsBlankCanvasSource(DependencyObject? source)
    {
        var taggedBorder = FindParent<Border>(source);
        return taggedBorder?.Tag is not long;
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
        _selectedNoteId = noteHits.Count > 0
            ? noteHits[0]
            : additive ? _marqueeBaselineNoteId : null;
        RenderVisibleItems();
    }

    private void EndMarquee()
    {
        _marqueeStartScreen = null;
        _marqueeActive = false;
        _marqueeBaseline.Clear();
        _marqueeBaselineNoteId = null;
        MarqueeSelection.Visibility = Visibility.Collapsed;
        if (Mouse.Captured == BoardViewport) BoardViewport.ReleaseMouseCapture();
    }

    private void BoardItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: long id } border) return;
        if (FindParent<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        _selectedNoteId = null;
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
        _cropModeActive = false;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _dragItemId = id;
            BeginRotationGesture(ToWorld(e.GetPosition(BoardViewport)));
            border.CaptureMouse();
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
        border.CaptureMouse();
        RenderVisibleItems();
        e.Handled = true;
    }

    private void BoardItemMouseMove(object sender, MouseEventArgs e)
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
        if (sender is Border border) border.ReleaseMouseCapture();
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

    private void NoteRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: long id }) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _selectedNoteId = _selectedNoteId == id ? null : id;
        else
        {
            _selectedNoteId = id;
            _selectedIds.Clear();
        }
        RenderVisibleItems();
    }

    private void NoteHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: long id } header) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
        if (note is null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _selectedNoteId = _selectedNoteId == id ? null : id;
        else
        {
            _selectedNoteId = id;
            _selectedIds.Clear();
        }
        if (_selectedNoteId != id)
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
        header.CaptureMouse();
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
        ReplaceNote(note with
        {
            X = _noteDragOrigin.X + current.X - _dragStartWorld.X,
            Y = _noteDragOrigin.Y + current.Y - _dragStartWorld.Y
        });
        RenderVisibleItems();
    }

    private async void NoteHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border header) header.ReleaseMouseCapture();
        if (_dragNoteId is not { } id) return;
        _dragNoteId = null;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
        if (_noteDragActive && note is not null) await SaveNoteAsync(note, "便签位置已保存");
        _noteDragActive = false;
        e.Handled = true;
    }

    private void NoteResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: long id }) return;
        _selectedNoteId = id;
        _selectedIds.Clear();
        _noteResizeOrigin = _notes.SingleOrDefault(candidate => candidate.Id == id);
        RenderVisibleItems();
    }

    private void NoteResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (_noteResizeOrigin is not { } origin) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == origin.Id);
        if (note is null) return;
        ReplaceNote(note with
        {
            Width = Math.Max(120, origin.Width + e.HorizontalChange / _viewport.Zoom),
            Height = Math.Max(100, origin.Height + e.VerticalChange / _viewport.Zoom)
        });
        _noteResizeOrigin = _notes.Single(candidate => candidate.Id == origin.Id);
        RenderVisibleItems();
    }

    private async void NoteResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_noteResizeOrigin is not { } note) return;
        _noteResizeOrigin = null;
        await SaveNoteAsync(note, "便签大小已保存");
    }

    private async void NoteEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: long id } editor) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
        if (note is null || note.Text == editor.Text) return;
        note = note with { Text = editor.Text };
        ReplaceNote(note);
        await SaveNoteAsync(note, "便签内容已保存");
    }

    private async Task SaveNoteAsync(BoardNoteRecord note, string status)
    {
        await _repository.UpdateBoardNoteAsync(CurrentBoardId, ToUpdate(note));
        SetStatus(status);
    }

    private async Task SaveSelectedItemsAsync()
    {
        try
        {
            var updates = _items
                .Where(item => _selectedIds.Contains(item.Id))
                .Select(ToUpdate)
                .ToArray();
            await _repository.UpdateBoardItemsAsync(CurrentBoardId, updates);
            SetStatus($"已保存 {_selectedIds.Count} 个画板项");
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-save", "Board item changes could not be saved.", ex);
            SetStatus($"保存失败：{ex.Message}");
        }
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

    private async void CropHorizontalClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.CropHorizontal);

    private async void CropVerticalClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.CropVertical);

    private async void ResetCropClick(object sender, RoutedEventArgs e) =>
        await ExecuteBoardCommandAsync(BoardCommandId.ResetCrop);

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

    private void PushUndoSnapshot() => CommitHistorySnapshot(SnapshotItems());

    private IReadOnlyList<BoardItemRecord> SnapshotItems() => _items.Select(item => item with { }).ToArray();

    private void CommitHistorySnapshot(IReadOnlyList<BoardItemRecord> snapshot)
    {
        _undo.Push(snapshot);
        while (_undo.Count > 50)
        {
            var retained = _undo.Reverse().Take(50).Reverse().ToArray();
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
        _redo.Push(SnapshotItems());
        var snapshot = _undo.Pop();
        await RestoreSnapshotAsync(snapshot, "已撤销");
    }

    private async void RedoClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.Redo);

    private async Task RedoAsync()
    {
        if (_redo.Count == 0) return;
        _undo.Push(SnapshotItems());
        var snapshot = _redo.Pop();
        await RestoreSnapshotAsync(snapshot, "已重做");
    }

    private async Task RestoreSnapshotAsync(IReadOnlyList<BoardItemRecord> snapshot, string status)
    {
        await _repository.ReplaceBoardItemsAsync(CurrentBoardId, snapshot);
        _items.Clear();
        _items.AddRange(await _repository.GetBoardItemsAsync(CurrentBoardId));
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
        _persistViewQueued = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cancellation.Token);
                await Dispatcher.InvokeAsync(async () => await PersistViewNowAsync());
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task PersistViewNowAsync(bool force = false)
    {
        if (!force && !_persistViewQueued && _boards.Count > 0) return;
        _persistViewQueued = false;
        var workingViewport = _focusController.WorkingViewport(_viewport);
        await _repository.UpdateBoardViewAsync(
            CurrentBoardId,
            workingViewport.OffsetX,
            workingViewport.OffsetY,
            workingViewport.Zoom);
    }

    private async void BackgroundSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingBoard || BackgroundSelector.SelectedValue is not string style) return;
        await _repository.UpdateBoardBackgroundAsync(CurrentBoardId, style);
        ApplyBackground(style);
        SetStatus("画板背景已保存");
    }

    private async void AddNoteClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.AddNote);

    private async Task AddNoteAsync()
    {
        await EnsureBoardLoadedAsync();
        var center = BoardViewportEngine.ScreenToWorld(
            _viewport,
            Math.Max(1, BoardViewport.ActualWidth) / 2,
            Math.Max(1, BoardViewport.ActualHeight) / 2);
        var z = _notes.Count == 0 ? 0 : _notes.Max(note => note.ZIndex) + 1;
        var note = await _repository.AddBoardNoteAsync(
            CurrentBoardId,
            "在这里记录灵感…",
            center.X - 150,
            center.Y - 110,
            300,
            220,
            z,
            "yellow");
        _notes.Add(note);
        _selectedIds.Clear();
        _selectedNoteId = note.Id;
        RenderVisibleItems();
        if (_realizedNotes.TryGetValue(note.Id, out var visual))
        {
            visual.Editor.Focus();
            visual.Editor.SelectAll();
        }
        SetStatus("已新建便签");
    }

    private async void CycleNoteColorClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.CycleNoteColor);

    private async Task CycleSelectedNoteColorAsync()
    {
        if (_selectedNoteId is not { } id) return;
        var note = _notes.SingleOrDefault(candidate => candidate.Id == id);
        if (note is null) return;
        var color = note.ColorStyle switch
        {
            "yellow" => "rose",
            "rose" => "blue",
            "blue" => "slate",
            _ => "yellow"
        };
        note = note with { ColorStyle = color };
        ReplaceNote(note);
        RenderVisibleItems();
        await SaveNoteAsync(note, "便签颜色已保存");
    }

    private async void DeleteNoteClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.DeleteNote);

    private async Task DeleteSelectedNoteAsync()
    {
        if (_selectedNoteId is not { } id) return;
        await _repository.DeleteBoardNotesAsync(CurrentBoardId, [id]);
        _notes.RemoveAll(note => note.Id == id);
        _selectedNoteId = null;
        RenderVisibleItems();
        SetStatus("便签已删除");
    }

    private async void NewBoardClick(object sender, RoutedEventArgs e) => await ExecuteBoardCommandAsync(BoardCommandId.NewBoard);

    private async Task CreateBoardAsync()
    {
        await PersistViewNowAsync(force: true);
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
            await PersistViewNowAsync(force: true);
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
        await PersistViewNowAsync(force: true);
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
        var textEditing = IsTextEditingFocus();
        if (textEditing)
        {
            if (e.Key == Key.Escape)
            {
                Keyboard.Focus(BoardViewport);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (ExitTransformMode())
            {
            }
            else if (_focusController.IsActive)
            {
                ToggleSelectionFocus();
            }
            else if (_marqueeStartScreen is not null)
            {
                EndMarquee();
            }
            else if (BoardInspector.Visibility == Visibility.Visible)
            {
                CloseInspector();
            }
            else if (_selectedIds.Count > 0 || _selectedNoteId is not null)
            {
                _selectedIds.Clear();
                _selectedNoteId = null;
                RenderVisibleItems();
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F10 || (e.Key == Key.System && e.SystemKey == Key.LeftAlt))
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
        else if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.ResetView);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ExecuteBoardCommandAsync(BoardCommandId.Copy);
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
                _selectedNoteId is not null ? BoardCommandId.DeleteNote : BoardCommandId.RemoveSelection);
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _selectedIds.Clear();
            _selectedNoteId = _notes.FirstOrDefault()?.Id;
            foreach (var item in _items) _selectedIds.Add(item.Id);
            RenderVisibleItems();
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CancelCameraAnimation();
        CancelProgressiveFocusLoad();
        _viewSaveCancellation?.Cancel();
        try
        {
            var workingViewport = _focusController.WorkingViewport(_viewport);
            _repository.UpdateBoardViewAsync(
                    CurrentBoardId,
                    workingViewport.OffsetX,
                    workingViewport.OffsetY,
                    workingViewport.Zoom)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Warning("board-close", "Final board view could not be saved.", ex);
        }
        base.OnClosing(e);
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

    private sealed record BoardNoteVisual(Border Root, TextBox Editor, Border Header);
}
