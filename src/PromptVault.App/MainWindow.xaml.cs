using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using System.Windows.Threading;
using PromptVault.App.Services;
using PromptVault.Core;
using Forms = System.Windows.Forms;

namespace PromptVault.App;

public partial class MainWindow : Window
{
    private readonly LibraryRepository _repository;
    private readonly CaptureCoordinator _capture;
    private readonly AppSettings _settings;
    private readonly AiWorkerLauncher _aiWorker;
    private readonly ExternalFolderIndexService _externalIndex;
    private readonly BoardWorkspaceService _boardWorkspace;
    private readonly ClipboardMonitor _clipboard;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly DispatcherTimer _subtleStatusTimer;
    private readonly DispatcherTimer _thumbnailIdleTimer;
    private readonly FastBrowseGenerationController _fastBrowseGenerations = new();
    private readonly HashSet<long> _selectedItemIds = new();
    private readonly Dictionary<GalleryRow, FrameworkElement> _realizedRowElements = new();
    private IReadOnlyList<CategoryRecord> _categories = [];
    private readonly List<GalleryEntry> _items = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _showTrash;
    private bool _favoritesOnly;
    private bool _allowClose;
    private bool _suppressFilterRefresh;
    private bool _suppressExternalRefresh;
    private bool _multiSelectMode;
    private bool _ignoreNextCardClick;
    private bool _startupRefreshPending = true;
    private double _layoutWidth;
    private readonly bool _trueTransparentWindow;
    private readonly MainWindowSnapshot? _initialSnapshot;
    private readonly bool _transitionStaging;
    private readonly TaskCompletionSource _transitionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _windowLifetime = new();
    private int _transitionPreparedRenderFrames;
    private bool _stagedHandoffCommitted;
    private bool _galleryRowsTransferredOut;
    private bool _restoredGallerySession;
    private int _galleryReflowCount;
    private int _transparentModeApplyCount;
    private int _transferredSessionRestoreCount;
    private string? _externalFolderId;
    private GalleryCardViewModel? _dragCandidate;
    private Point _dragStart;
    private ScrollViewer? _rowsScrollViewer;
    private readonly DevelopmentFrameSampler _frameSampler = new();
    private SearchOptions? _activeSearch;
    private GalleryPageCursor? _nextPageCursor;
    private ExternalFilePageCursor? _nextExternalPageCursor;
    private IReadOnlyDictionary<string, ExternalFolderIndexState> _externalFolderStates =
        new Dictionary<string, ExternalFolderIndexState>();
    private long _totalCount;
    private bool _hasMoreItems;
    private bool _isLoadingNextPage;
    private bool _thumbnailPriorityRefreshQueued;
    private FastBrowseGenerationLease _fastBrowseLease;
    private AdaptiveFastBrowsePlan _fastBrowsePlan = AdaptiveFastBrowsePolicy.Create(
        0,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    private GalleryScrollDirection _galleryScrollDirection;
    private double _lastGalleryVerticalOffset;
    private EventHandler? _developmentScrollProbeRendering;
    private const double GalleryWheelPixelsPerNotch = 180d;

    public BulkObservableCollection<GalleryRow> Rows { get; } = new();

    internal MainWindow(
        LibraryRepository repository,
        CaptureCoordinator capture,
        AppSettings settings,
        ExternalFolderIndexService externalIndex,
        BoardWorkspaceService boardWorkspace,
        bool transparentWindow = false,
        MainWindowSnapshot? initialSnapshot = null,
        bool transitionStaging = false)
    {
        _trueTransparentWindow = transparentWindow;
        _transparentMode = transparentWindow;
        _initialSnapshot = initialSnapshot;
        _transitionStaging = transitionStaging;
        if (transparentWindow)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
        }

        _repository = repository;
        _capture = capture;
        _settings = settings;
        _aiWorker = new AiWorkerLauncher(repository, settings);
        _externalIndex = externalIndex;
        _boardWorkspace = boardWorkspace;
        _externalIndex.IndexChanged += ExternalFolderIndexChanged;
        DataContext = this;
        if (!transitionStaging) VisualModeService.Apply(transparentWindow, settings.ReducedMotionEnabled);
        InitializeComponent();
        LostMouseCapture += (_, _) => { if (_ctrlRightDragging) EndCtrlRightDrag(); };
        UpdateAppearanceResources();
        UpdateInspectorPinVisual();
        UpdateLayoutControlVisuals();
        CategoryList.ContextMenu = new System.Windows.Controls.ContextMenu();
        ExternalFolderList.ContextMenu = new System.Windows.Controls.ContextMenu();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshAsync(RefreshAnimationKind.Search); };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); RegroupIfNeeded(); };
        _subtleStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _subtleStatusTimer.Tick += (_, _) => { _subtleStatusTimer.Stop(); UpdateBaseStatus(); };
        _thumbnailIdleTimer = new DispatcherTimer
        {
            Interval = AdaptiveFastBrowsePolicy.IdleHighQualityDelay
        };
        _thumbnailIdleTimer.Tick += (_, _) =>
        {
            _thumbnailIdleTimer.Stop();
            UpgradeVisibleThumbnailsAfterIdle();
        };
        _clipboard = new ClipboardMonitor(
            this,
            repository,
            capture,
            () => _categories,
            () => _settings.CaptureQuickEditEnabled,
            SaveCaptureAsync,
            RefreshAfterCaptureChangeAsync);
        _clipboard.SetEnabled(!transitionStaging && _settings.CaptureListeningEnabled);
        UpdateCaptureToggleVisual();
        UpdateQuickCaptureModeVisual();
        UpdateSelectionVisual();
        Loaded += async (_, _) =>
        {
            try
            {
                using var startupMeasurement = DevelopmentPerformanceTrace.Measure("gallery-first-content-ready");
                try
                {
                    await LoadCategoriesAsync();
                    _windowLifetime.Token.ThrowIfCancellationRequested();
                    await LoadExternalFoldersAsync();
                    _windowLifetime.Token.ThrowIfCancellationRequested();
                    ApplyInitialSnapshot();
                    UpdateLayoutControlVisuals();
                    ApplyTransparentMode(applyVisualModeResources: !transitionStaging);
                    _restoredGallerySession = RestoreTransferredGallerySession();
                    if (!_restoredGallerySession)
                    {
                        await RefreshAsync(RefreshAnimationKind.None);
                    }
                    RestoreViewerFromSnapshot();
                }
                finally
                {
                    _startupRefreshPending = false;
                    _searchTimer.Stop();
                    _resizeTimer.Stop();
                    if (!_restoredGallerySession) RegroupIfNeeded();
                    if (DevelopmentPerformanceTrace.AutoRunScrollProbeCount > 0)
                    {
                        _ = Dispatcher.BeginInvoke(
                            DispatcherPriority.ApplicationIdle,
                            new Action(async () =>
                            {
                                await Task.Delay(1500);
                                for (var probe = 0;
                                     probe < DevelopmentPerformanceTrace.AutoRunScrollProbeCount;
                                     probe++)
                                {
                                    StartDevelopmentScrollProbe();
                                    if (probe + 1 < DevelopmentPerformanceTrace.AutoRunScrollProbeCount)
                                    {
                                        await Task.Delay(5000);
                                    }
                                }
                            }));
                    }
                }
                if (transitionStaging) await PrepareTransitionHandoffAsync();
                _transitionReady.TrySetResult();
            }
            catch (Exception ex)
            {
                _transitionReady.TrySetException(ex);
                if (!transitionStaging) throw;
            }
        };
        SizeChanged += (_, _) =>
        {
            if (_startupRefreshPending) return;
            StabilizeHiddenPanelsForResize();
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
    }

    private bool IsExternalMode => _externalFolderId is not null;

    internal MainWindowSnapshot CreateSnapshot()
    {
        var categoryId = (CategoryList.SelectedItem as CategoryChoice)?.Id;
        long? viewerItemId = _viewerIndex >= 0 && _viewerIndex < _items.Count ? _items[_viewerIndex].Id : null;
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        var session = new GallerySessionSnapshot(
            _items.ToArray(),
            Rows.ToArray(),
            _layoutWidth,
            _totalCount,
            _hasMoreItems,
            _activeSearch,
            _nextPageCursor,
            _nextExternalPageCursor,
            _isFastBrowseIndexing,
            _rowsScrollViewer?.VerticalOffset ?? 0);
        return new MainWindowSnapshot(
            Left,
            Top,
            Width,
            Height,
            WindowState,
            Topmost,
            SearchBox.Text,
            TagBox.Text,
            categoryId,
            _externalFolderId,
            _showTrash,
            _favoritesOnly,
            _oldestFirst,
            _multiSelectMode,
            _selectedItemIds.ToArray(),
            viewerItemId,
            _inspectorVisible,
            InspectorColumn.ActualWidth > 0 ? InspectorColumn.ActualWidth : _settings.InspectorWidth,
            session);
    }

    private void ApplyInitialSnapshot()
    {
        if (_initialSnapshot is not { } snapshot) return;
        if (snapshot.Width > 0) Width = snapshot.Width;
        if (snapshot.Height > 0) Height = snapshot.Height;
        if (!double.IsNaN(snapshot.Left)) Left = snapshot.Left;
        if (!double.IsNaN(snapshot.Top)) Top = snapshot.Top;
        WindowState = snapshot.WindowState;
        Topmost = snapshot.Topmost;
        SearchBox.Text = snapshot.SearchText;
        TagBox.Text = snapshot.TagText;
        _showTrash = snapshot.ShowTrash;
        _favoritesOnly = snapshot.FavoritesOnly;
        _oldestFirst = snapshot.OldestFirst;
        _multiSelectMode = snapshot.MultiSelectMode;
        _externalFolderId = snapshot.ExternalFolderId;
        _selectedItemIds.Clear();
        foreach (var id in snapshot.SelectedItemIds) _selectedItemIds.Add(id);
        RestoreInspectorGeometry(snapshot);

        if (_externalFolderId is not null && ExternalFolderList.ItemsSource is IEnumerable<ExternalFolderChoice> externalChoices)
        {
            var external = externalChoices.FirstOrDefault(x => x.Id == _externalFolderId);
            if (external is not null)
            {
                _suppressExternalRefresh = true;
                ExternalFolderList.SelectedItem = external;
                _suppressExternalRefresh = false;
                _suppressFilterRefresh = true;
                CategoryList.SelectedIndex = -1;
                _suppressFilterRefresh = false;
            }
        }
        else if (CategoryList.ItemsSource is IEnumerable<CategoryChoice> choices)
        {
            var match = choices.FirstOrDefault(x => x.Id == snapshot.CategoryId);
            if (match is not null)
            {
                _suppressFilterRefresh = true;
                CategoryList.SelectedItem = match;
                _suppressFilterRefresh = false;
            }
        }

        UpdatePinVisual();
        UpdateTrashVisual();
        UpdateSelectionVisual();
    }

    private void RestoreViewerFromSnapshot()
    {
        if (_initialSnapshot?.ViewerItemId is long itemId) ShowImmersiveViewer(itemId);
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            ToastService.Show(this, _clipboard.IsEnabled ? "FR_Imageprompt \u5DF2\u5728\u6258\u76D8\u7EE7\u7EED\u76D1\u542C" : "FR_Imageprompt \u5DF2\u5728\u6258\u76D8\u7EE7\u7EED\u8FD0\u884C");
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowLifetime.Cancel();
        ReleaseHandoffInput();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _fastBrowseGenerations.Dispose();
        _searchTimer.Stop();
        _resizeTimer.Stop();
        _subtleStatusTimer.Stop();
        _thumbnailIdleTimer.Stop();
        _edgeIntentTimer.Stop();
        _topHideTimer.Stop();
        _leftHideTimer.Stop();
        _appearanceReflowTimer?.Stop();
        _inspectorResizeTimer?.Stop();
        _inspectorSavedTimer.Stop();
        _inspectorSavedTimer.Tick -= InspectorSavedTimerTick;
        _transparentSelectionFadeTimer?.Stop();
        _transparentSelectionAnimationTimer?.Stop();
        _transparentSelectionAnimationTimer?.Tick -= TransparentSelectionAnimationTick;
        if (_developmentScrollProbeRendering is not null)
        {
            CompositionTarget.Rendering -= _developmentScrollProbeRendering;
            _developmentScrollProbeRendering = null;
        }
        StopFastBrowseBackground();
        if (!_galleryRowsTransferredOut)
        {
            foreach (var row in Rows) ReleaseRow(row);
        }
        // A handoff reuses the row view-models in the replacement window. Detach
        // the retired ItemsControls before dropping our collections so their
        // collection views cannot keep the closed visual tree alive.
        RowsList.ItemsSource = null;
        CategoryList.ItemsSource = null;
        ExternalFolderList.ItemsSource = null;
        _realizedRowElements.Clear();
        Rows.Clear();
        _items.Clear();
        _frameSampler.Dispose();
        _clipboard.Dispose();
        _externalIndex.IndexChanged -= ExternalFolderIndexChanged;
        DataContext = null;
        Content = null;
        base.OnClosed(e);
    }

    private async Task LoadCategoriesAsync()
    {
        if (_settings.CaptureQuickEditEnabled)
        {
            await EnsureQuickCaptureCategoriesAsync();
        }
        _categories = await _repository.GetCategoriesAsync();
        var choices = new List<CategoryChoice> { new(null, "\u5168\u90E8\u56FE\u7247") };
        choices.Add(new CategoryChoice(0, "\u672A\u5206\u7C7B"));
        choices.AddRange(_categories.Select(x => new CategoryChoice(x.Id, x.Name)));
        CategoryList.ItemsSource = choices;
        _suppressFilterRefresh = true;
        CategoryList.SelectedIndex = 0;
        _suppressFilterRefresh = false;
    }

    private async Task LoadExternalFoldersAsync(CancellationToken cancellationToken = default)
    {
        _externalFolderStates = await _repository.GetExternalFolderIndexStatesAsync(
            cancellationToken);
        var choices = _settings.ExternalFolders
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new ExternalFolderChoice(
                x.Id,
                string.IsNullOrWhiteSpace(x.Name)
                    ? System.IO.Path.GetFileName(x.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                    : x.Name,
                x.Path,
                FormatExternalFolderStatus(_externalFolderStates.GetValueOrDefault(x.Id))))
            .ToArray();
        _suppressExternalRefresh = true;
        ExternalFolderList.ItemsSource = choices;
        if (_externalFolderId is not null)
        {
            ExternalFolderList.SelectedItem = choices.FirstOrDefault(x => x.Id == _externalFolderId);
        }
        _suppressExternalRefresh = false;
    }

    internal Task TransitionReady => _transitionReady.Task;

    internal int TransitionPreparedRenderFrames => _transitionPreparedRenderFrames;

    internal void CommitStagedVisualMode()
    {
        ApplyTransparentMode();
        _clipboard.SetEnabled(_settings.CaptureListeningEnabled);
        UpdateCaptureToggleVisual();
        _stagedHandoffCommitted = true;
    }

    internal void AbandonStagedWindow()
    {
        _windowLifetime.Cancel();
        _transitionReady.TrySetCanceled(_windowLifetime.Token);
        if (_initialSnapshot?.GallerySession is not null) _galleryRowsTransferredOut = true;
    }

    private async Task PrepareTransitionHandoffAsync()
    {
        var expectedOffset = _initialSnapshot?.GallerySession?.VerticalOffset;
        for (var frame = 0; frame < 2; frame++)
        {
            await WaitForRenderFrameAsync();
            _transitionPreparedRenderFrames++;
            if (expectedOffset is { } offset)
            {
                _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
                if (_rowsScrollViewer is not null
                    && Math.Abs(_rowsScrollViewer.VerticalOffset - offset) > 0.5)
                {
                    _rowsScrollViewer.ScrollToVerticalOffset(offset);
                    UpdateLayout();
                }
            }
        }
    }

    private async Task WaitForRenderFrameAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _windowLifetime.Token;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            ready.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        using var registration = token.Register(() => ready.TrySetCanceled(token));
        try { await ready.Task; }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static string FormatExternalFolderStatus(ExternalFolderIndexState? state)
    {
        if (state is null) return "等待建立索引";
        return state.Status switch
        {
            ExternalFolderIndexStatus.Pending => "等待建立索引",
            ExternalFolderIndexStatus.Indexing => $"正在建立索引 · 已发现 {state.AvailableFiles:N0} 张",
            ExternalFolderIndexStatus.Missing => "文件夹不可用",
            ExternalFolderIndexStatus.PermissionDenied => "没有读取权限",
            ExternalFolderIndexStatus.Failed => "索引失败",
            _ when state.FailedFiles > 0 =>
                $"{state.AvailableFiles:N0} 张 · {state.FailedFiles:N0} 个文件无法读取",
            _ => $"{state.AvailableFiles:N0} 张"
        };
    }

    private void ExternalFolderIndexChanged(
        object? sender,
        ExternalFolderIndexChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try
            {
                await LoadExternalFoldersAsync();
                if (e.ContentChanged
                    && IsLoaded
                    && string.Equals(_externalFolderId, e.FolderId, StringComparison.Ordinal))
                {
                    await RefreshAsync(RefreshAnimationKind.ContentChange);
                }
                else
                {
                    UpdateBaseStatus();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning(
                    "external-folder-index-ui",
                    "External folder index state could not be applied.",
                    ex);
            }
        }));
    }

    private async Task RefreshAsync(RefreshAnimationKind animationKind = RefreshAnimationKind.ContentChange)
    {
        if (_galleryRowsTransferredOut || _windowLifetime.IsCancellationRequested) return;
        _activeGalleryRefresh = RefreshCoreAsync(animationKind);
        await _activeGalleryRefresh;
    }

    private async Task RefreshCoreAsync(RefreshAnimationKind animationKind)
    {
        if (_inspectorVisible && _inspectorPromptDirty)
        {
            if (!await SaveInspectorOrdinaryFieldsAsync())
            {
                InspectorTagsEditor.Focus();
                return;
            }
            if (!await ResolveDirtyPromptBeforeSwitchAsync())
            {
                InspectorPromptEditor.Focus();
                return;
            }
        }
        _aiWorker.MarkUiActive();
        var refreshStopwatch = Stopwatch.StartNew();
        using var measurement = DevelopmentPerformanceTrace.Measure("gallery-refresh", new
        {
            animation = animationKind.ToString(),
            queryLength = SearchBox.Text.Length,
            hasTagFilter = !string.IsNullOrWhiteSpace(TagBox.Text),
            external = IsExternalMode
        });
        var previousCancellation = _loadCancellation;
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        _fastBrowseLease = _fastBrowseGenerations.Begin(token);
        try
        {
            StatusText.Text = "正在加载图片…";
            var previousItemCount = _items.Count;
            DevelopmentPerformanceTrace.Event("gallery-refresh-transition", new
            {
                phase = "query-start",
                reason = animationKind.ToString(),
                displayedItems = _items.Count,
                displayedRows = Rows.Count,
                rowsOpacity = RowsList.Opacity
            });
            var queryStopwatch = Stopwatch.StartNew();
            IReadOnlyList<GalleryEntry> result;
            long totalCount;
            GalleryPageCursor? nextCursor = null;
            ExternalFilePageCursor? nextExternalCursor = null;
            var selected = CategoryList.SelectedItem as CategoryChoice;
            var search = new SearchOptions(
                Query: SearchBox.Text,
                CategoryId: selected?.Id is > 0 ? selected.Id : null,
                UncategorizedOnly: selected?.Id == 0,
                Tag: TagBox.Text,
                Trash: _showTrash ? GalleryTrashScope.Trash : GalleryTrashScope.Active,
                Source: IsExternalMode ? GallerySourceKind.ExternalFolder : GallerySourceKind.Library,
                SourceId: IsExternalMode ? _externalFolderId : null,
                FavoritesOnly: !IsExternalMode && _favoritesOnly,
                Sort: _oldestFirst ? GallerySortOrder.OldestFirst : GallerySortOrder.NewestFirst,
                PageSize: GalleryVirtualizationPolicy.PageSize);
            if (IsExternalMode)
            {
                var folder = _settings.ExternalFolders.FirstOrDefault(x => x.Id == _externalFolderId);
                if (folder is null)
                {
                    result = [];
                    totalCount = 0;
                }
                else
                {
                    await _externalIndex.EnsureFolderIndexedAsync(folder, cancellationToken: token);
                    var page = await _repository.SearchExternalFilesAsync(
                        folder.Id,
                        search.Query,
                        search.Sort,
                        search.PageSize,
                        cancellationToken: token);
                    result = page.Items.Select(item => GalleryEntry.FromExternal(item, folder.Path)).ToArray();
                    totalCount = page.TotalCount;
                    nextExternalCursor = page.NextCursor;
                    var state = await _externalIndex.GetStateAsync(folder.Id, token);
                    if (state is not null)
                    {
                        _externalFolderStates = new Dictionary<string, ExternalFolderIndexState>(
                            _externalFolderStates,
                            StringComparer.Ordinal)
                        {
                            [folder.Id] = state
                        };
                    }
                }
            }
            else
            {
                var page = await _repository.SearchPageAsync(search, token);
                result = page.Items.Select(GalleryEntry.FromLibrary).ToArray();
                totalCount = page.TotalCount;
                nextCursor = page.NextCursor;
            }

            token.ThrowIfCancellationRequested();
            queryStopwatch.Stop();
            var nextItems = result.ToArray();
            var availableWidth = GetGalleryAvailableWidth();
            var nextRows = GalleryLayoutEngine.CreateRows(
                nextItems,
                availableWidth,
                CurrentGalleryLayoutOptions());
            var preparationStopwatch = Stopwatch.StartNew();
            var preparation = await PrepareFirstViewportAsync(
                nextRows,
                nextItems,
                token);
            preparationStopwatch.Stop();
            token.ThrowIfCancellationRequested();
            DevelopmentPerformanceTrace.Event("gallery-refresh-transition", new
            {
                phase = "first-viewport-ready",
                reason = animationKind.ToString(),
                displayedItems = _items.Count,
                displayedRows = Rows.Count,
                preparedItems = nextItems.Length,
                preparedRows = preparation.PreparedRows,
                rowsOpacity = RowsList.Opacity
            });

            _activeSearch = search;
            _nextPageCursor = nextCursor;
            _nextExternalPageCursor = nextExternalCursor;
            _totalCount = totalCount;
            _fastBrowsePlan = AdaptiveFastBrowsePolicy.Create(
                totalCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            _hasMoreItems = IsExternalMode
                ? nextExternalCursor is not null
                : nextCursor is not null;
            _isLoadingNextPage = false;
            _selectedItemIds.RemoveWhere(id => nextItems.All(item => item.Id != id));

            var applyStopwatch = Stopwatch.StartNew();
            var applyResult = ApplyPreparedRows(
                nextItems,
                nextRows,
                availableWidth,
                preparation,
                resetScroll: true);
            applyStopwatch.Stop();
            _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
            UpdateCountText();
            UpdateBaseStatus();
            UpdateTrashVisual();
            UpdateSelectionVisual();
            RestoreInspectorAfterRefresh();
            QueueNextPageIfNeeded();
            StartFastBrowseBackground(search, _fastBrowseLease);
            refreshStopwatch.Stop();
            DevelopmentPerformanceTrace.Event("gallery-refresh-applied", new
            {
                reason = animationKind.ToString(),
                previousItems = previousItemCount,
                nextItems = nextItems.Length,
                queryMs = Math.Round(queryStopwatch.Elapsed.TotalMilliseconds, 3),
                preparationMs = Math.Round(preparationStopwatch.Elapsed.TotalMilliseconds, 3),
                applyMs = Math.Round(applyStopwatch.Elapsed.TotalMilliseconds, 3),
                totalMs = Math.Round(refreshStopwatch.Elapsed.TotalMilliseconds, 3),
                applyResult.ReusedRows,
                applyResult.ReusedCards,
                applyResult.PreparedCards,
                applyResult.PreparedRows,
                applyResult.FirstViewportCards,
                applyResult.ReadyFirstViewportCards,
                rowsOpacity = RowsList.Opacity
            });
        }
        catch (OperationCanceledException)
        {
            refreshStopwatch.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Error("gallery-refresh", ex);
            StatusText.Text = $"\u52A0\u8F7D\u5931\u8D25\uFF1A{ex.Message}";
        }
    }

    private async Task LoadNextPageAsync()
    {
        if (_isFastBrowseIndexing
            || _isLoadingNextPage
            || !_hasMoreItems
            || _activeSearch is null)
        {
            return;
        }
        var cancellation = _loadCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return;

        _isLoadingNextPage = true;
        ShowSubtleStatus("正在准备下一批…");
        var token = cancellation.Token;
        var continuePrefetch = true;
        try
        {
            IReadOnlyList<GalleryEntry> nextItems;
            long totalCount;
            GalleryPageCursor? nextCursor = null;
            ExternalFilePageCursor? nextExternalCursor = null;
            if (_activeSearch.Source == GallerySourceKind.ExternalFolder)
            {
                var folder = _settings.ExternalFolders.FirstOrDefault(x => x.Id == _activeSearch.SourceId);
                if (folder is null || _nextExternalPageCursor is null)
                {
                    _hasMoreItems = false;
                    return;
                }
                var page = await _repository.SearchExternalFilesAsync(
                    folder.Id,
                    _activeSearch.Query,
                    _activeSearch.Sort,
                    _activeSearch.PageSize,
                    _nextExternalPageCursor,
                    token);
                nextItems = page.Items.Select(item => GalleryEntry.FromExternal(item, folder.Path)).ToArray();
                totalCount = page.TotalCount;
                nextExternalCursor = page.NextCursor;
            }
            else
            {
                if (_nextPageCursor is null)
                {
                    _hasMoreItems = false;
                    return;
                }

                var page = await _repository.SearchPageAsync(
                    _activeSearch with { Cursor = _nextPageCursor },
                    token);
                nextItems = page.Items.Select(GalleryEntry.FromLibrary).ToArray();
                totalCount = page.TotalCount;
                nextCursor = page.NextCursor;
            }

            token.ThrowIfCancellationRequested();
            var knownIds = _items.Select(item => item.Id).ToHashSet();
            var uniqueItems = nextItems.Where(item => knownIds.Add(item.Id)).ToArray();
            _totalCount = totalCount;
            _nextPageCursor = nextCursor;
            _nextExternalPageCursor = nextExternalCursor;
            _hasMoreItems = _activeSearch.Source == GallerySourceKind.ExternalFolder
                ? nextExternalCursor is not null
                : nextCursor is not null;

            if (uniqueItems.Length > 0)
            {
                _items.AddRange(uniqueItems);
                AppendGalleryRows(uniqueItems);
            }

            UpdateCountText();
            DevelopmentPerformanceTrace.Event("gallery-page-applied", new
            {
                loadedItems = _items.Count,
                totalItems = _totalCount,
                rowCount = Rows.Count,
                realizedCards = Rows.Sum(row => row.Items.Count),
                hasMore = _hasMoreItems
            });
        }
        catch (OperationCanceledException)
        {
            continuePrefetch = false;
        }
        catch (Exception ex)
        {
            continuePrefetch = false;
            AppLog.Error("gallery-next-page", ex);
            ShowSubtleStatus($"继续加载失败：{ex.Message}");
        }
        finally
        {
            _isLoadingNextPage = false;
        }

        if (continuePrefetch) QueueNextPageIfNeeded();
    }

    private void QueueNextPageIfNeeded()
    {
        if (_galleryRowsTransferredOut || _windowLifetime.IsCancellationRequested) return;
        if (_isFastBrowseIndexing || !_hasMoreItems || _isLoadingNextPage) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_galleryRowsTransferredOut || _windowLifetime.IsCancellationRequested) return;
            _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
            if (_rowsScrollViewer is not null && ShouldPrefetchNextPage(_rowsScrollViewer))
            {
                _ = LoadNextPageAsync();
            }
        }));
    }

    private static bool ShouldPrefetchNextPage(ScrollViewer viewer)
    {
        return GalleryVirtualizationPolicy.ShouldPrefetch(
            viewer.ViewportHeight,
            viewer.ScrollableHeight,
            viewer.VerticalOffset);
    }

    private void UpdateCountText()
    {
        var countLabel = IsExternalMode
            ? "\u5916\u90E8\u6587\u4EF6\u5939"
            : _showTrash ? "\u56DE\u6536\u7AD9" : _favoritesOnly ? "已收藏图片" : "\u56FE\u7247\u6536\u85CF";
        CountText.Text = _items.Count < _totalCount
            ? $"{countLabel} - {_totalCount:N0}\uFF08\u5DF2\u52A0\u8F7D {_items.Count:N0}\uFF09"
            : $"{countLabel} - {_totalCount:N0}";
    }

    private void AppendGalleryRows(IReadOnlyList<GalleryEntry> appendedItems)
    {
        if (appendedItems.Count == 0) return;
        var availableWidth = GetGalleryAvailableWidth();
        if (Math.Abs(availableWidth - _layoutWidth) >= 32)
        {
            var regrouped = GalleryLayoutEngine.CreateRows(
                _items,
                availableWidth,
                CurrentGalleryLayoutOptions());
            var reusableCards = CreateReusableCardMap(_items);
            ApplyPreparedRows(
                _items.ToArray(),
                regrouped,
                availableWidth,
                new GalleryPreparation(
                    reusableCards,
                    new Dictionary<long, GalleryCardViewModel>(),
                    CountRowsForFirstViewport(regrouped)),
                resetScroll: false);
            return;
        }

        var append = GalleryLayoutEngine.CreateAppend(
            Rows,
            appendedItems,
            _layoutWidth,
            CurrentGalleryLayoutOptions());
        if (append.ReplaceIncompleteTail && Rows.LastOrDefault() is { } incomplete)
        {
            var replacement = append.Rows[0];
            var reusableCards = incomplete.Items.ToDictionary(card => card.Id);
            var transferredCards = new HashSet<GalleryCardViewModel>();
            if (incomplete.IsRealized)
            {
                replacement.Realize(
                    _repository.Paths,
                    id => _selectedItemIds.Contains(id),
                    reusableCards,
                    transferredCards);
            }

            ReleaseRow(incomplete, transferredCards);
            Rows[^1] = replacement;
            foreach (var row in append.Rows.Skip(1)) Rows.Add(row);
        }
        else
        {
            foreach (var row in append.Rows) Rows.Add(row);
        }

        InvalidateMasonryLayoutIndex();
        QueueThumbnailPriorityRefresh();
    }

    private async Task<GalleryPreparation> PrepareFirstViewportAsync(
        IReadOnlyList<GalleryRow> rows,
        IReadOnlyList<GalleryEntry> nextItems,
        CancellationToken cancellationToken)
    {
        var reusableCards = CreateReusableCardMap(nextItems);
        var preparedCards = new Dictionary<long, GalleryCardViewModel>();
        var loadTasks = new List<Task>();
        var preparedRows = CountRowsForFirstViewport(rows);
        var dpiScale = VisualTreeHelper.GetDpi(RowsList).DpiScaleX;

        for (var rowIndex = 0; rowIndex < preparedRows; rowIndex++)
        {
            foreach (var layout in rows[rowIndex].LayoutItems)
            {
                if (reusableCards.TryGetValue(layout.Item.Id, out var reusableCard)
                    && (reusableCard.Thumbnail is not null || reusableCard.ThumbnailLoadFailed))
                {
                    continue;
                }

                if (reusableCard is not null)
                {
                    // An unfinished card cannot satisfy the first-viewport-ready
                    // contract. Prepare a replacement without mutating the card
                    // that is still presenting the old gallery.
                    reusableCards.Remove(layout.Item.Id);
                }

                if (preparedCards.ContainsKey(layout.Item.Id))
                {
                    continue;
                }

                var card = new GalleryCardViewModel(
                    layout.Item,
                    _repository.Paths,
                    layout.LayoutX,
                    layout.LayoutY,
                    layout.LayoutWidth,
                    layout.ImageHeight,
                    _selectedItemIds.Contains(layout.Item.Id));
                preparedCards.Add(layout.Item.Id, card);
                loadTasks.Add(card.PrepareAsync(
                    ThumbnailRequestPriority.Visible,
                    dpiScale,
                    cancellationToken));
            }
        }

        try
        {
            await Task.WhenAll(loadTasks);
            return new GalleryPreparation(reusableCards, preparedCards, preparedRows);
        }
        catch
        {
            foreach (var card in preparedCards.Values) card.CancelThumbnailLoad();
            throw;
        }
    }

    private GalleryApplyResult ApplyPreparedRows(
        IReadOnlyList<GalleryEntry> nextItems,
        IReadOnlyList<GalleryRow> desiredRows,
        double availableWidth,
        GalleryPreparation preparation,
        bool resetScroll,
        bool bulkRows = false)
    {
        var oldRows = Rows.ToArray();
        var targetRows = new List<GalleryRow>(desiredRows.Count);
        var reusedRows = 0;
        for (var index = 0; index < desiredRows.Count; index++)
        {
            var desired = desiredRows[index];
            if (index < oldRows.Length && oldRows[index].CanReuseFrom(desired))
            {
                oldRows[index].UpdateFrom(desired, id => _selectedItemIds.Contains(id));
                targetRows.Add(oldRows[index]);
                reusedRows++;
            }
            else
            {
                targetRows.Add(desired);
            }
        }

        var candidateCards = new Dictionary<long, GalleryCardViewModel>(preparation.ReusableCards);
        foreach (var (id, card) in preparation.PreparedCards)
        {
            candidateCards.TryAdd(id, card);
        }

        var transferredCards = new HashSet<GalleryCardViewModel>();
        var additionalRows = preparation.AdditionalRows is null
            ? Enumerable.Empty<int>()
            : preparation.AdditionalRows;
        var rowsToRealize = Enumerable
            .Range(0, Math.Min(preparation.PreparedRows, targetRows.Count))
            .Concat(additionalRows)
            .Where(index => index >= 0 && index < targetRows.Count)
            .Distinct()
            .Order();
        foreach (var index in rowsToRealize)
        {
            targetRows[index].Realize(
                _repository.Paths,
                id => _selectedItemIds.Contains(id),
                candidateCards,
                transferredCards);
        }

        var targetRowSet = targetRows.ToHashSet();
        foreach (var oldRow in oldRows)
        {
            if (!targetRowSet.Contains(oldRow)) ReleaseRow(oldRow, transferredCards);
        }

        _items.Clear();
        _items.AddRange(nextItems);
        _layoutWidth = availableWidth;
        if (bulkRows)
        {
            Rows.ReplaceAll(targetRows);
        }
        else
        {
            for (var index = 0; index < targetRows.Count; index++)
            {
                if (index < Rows.Count)
                {
                    if (!ReferenceEquals(Rows[index], targetRows[index])) Rows[index] = targetRows[index];
                }
                else
                {
                    Rows.Add(targetRows[index]);
                }
            }

            while (Rows.Count > targetRows.Count) Rows.RemoveAt(Rows.Count - 1);
        }

        InvalidateMasonryLayoutIndex();
        RecordM112StartupSample("apply");
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        if (resetScroll) _rowsScrollViewer?.ScrollToTop();
        EmptyGalleryState.Visibility = nextItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        QueueThumbnailPriorityRefresh();

        var reusedCardSet = preparation.ReusableCards.Values.ToHashSet();
        var preparedCardSet = preparation.PreparedCards.Values.ToHashSet();
        var realizedCards = targetRows
            .Take(preparation.PreparedRows)
            .SelectMany(row => row.Items)
            .ToArray();
        return new GalleryApplyResult(
            reusedRows,
            realizedCards.Count(reusedCardSet.Contains),
            realizedCards.Count(preparedCardSet.Contains),
            preparation.PreparedRows,
            realizedCards.Length,
            realizedCards.Count(card => card.Thumbnail is not null || card.ThumbnailLoadFailed));
    }

    private Dictionary<long, GalleryCardViewModel> CreateReusableCardMap(
        IReadOnlyList<GalleryEntry> nextItems)
    {
        var nextById = nextItems.ToDictionary(item => item.Id);
        var cards = new Dictionary<long, GalleryCardViewModel>();
        foreach (var card in Rows.SelectMany(row => row.Items))
        {
            if (nextById.TryGetValue(card.Id, out var next)
                && card.CanReuseFor(next))
            {
                cards.TryAdd(card.Id, card);
            }
        }
        return cards;
    }

    private int CountRowsForFirstViewport(IReadOnlyList<GalleryRow> rows)
    {
        var targetHeight = RowsList.ActualHeight > 0
            ? RowsList.ActualHeight
            : Math.Max(720, ActualHeight - 140);
        var count = 0;
        while (count < rows.Count && rows[count].PanelY < targetHeight)
        {
            count++;
        }
        return Math.Min(rows.Count, Math.Max(1, count));
    }

    private double GetGalleryAvailableWidth()
    {
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        var viewportWidth = _rowsScrollViewer?.ViewportWidth ?? double.NaN;
        var hostWidth = RowsList.ActualWidth >= GalleryViewportWidthPolicy.MinimumWidth
            ? RowsList.ActualWidth
            : GalleryColumn.ActualWidth >= GalleryViewportWidthPolicy.MinimumWidth
                ? GalleryColumn.ActualWidth - 40
                : ActualWidth - 56;
        return GalleryViewportWidthPolicy.Calculate(
            viewportWidth,
            hostWidth,
            SystemParameters.VerticalScrollBarWidth);
    }

    private void InvalidateMasonryLayoutIndex()
    {
        var panel = FindDescendant<VirtualizingMasonryPanel>(RowsList);
        if (panel is not null)
        {
            panel.InvalidateLayoutIndex();
        }
        else
        {
            RowsList.InvalidateMeasure();
        }
    }

    private void ReleaseRow(
        GalleryRow row,
        ISet<GalleryCardViewModel>? preservedCards = null)
    {
        _realizedRowElements.Remove(row);
        row.Release(preservedCards);
    }

    private void RegroupIfNeeded()
    {
        if (_galleryRowsTransferredOut || _windowLifetime.IsCancellationRequested) return;
        var width = GetGalleryAvailableWidth();
        if (Math.Abs(width - _layoutWidth) < 32) return;
        _galleryReflowCount++;
        var rows = GalleryLayoutEngine.CreateRows(
            _items,
            width,
            CurrentGalleryLayoutOptions());
        ApplyPreparedRows(
            _items.ToArray(),
            rows,
            width,
            new GalleryPreparation(
                CreateReusableCardMap(_items),
                new Dictionary<long, GalleryCardViewModel>(),
                CountRowsForFirstViewport(rows)),
            resetScroll: false);
    }

    private async Task<CaptureSaveResult> SaveCaptureAsync(
        PendingCapture pending,
        string prompt,
        string notes,
        long? category,
        string tags)
    {
        var result = await _capture.SaveAsync(pending, prompt, notes, category, [tags]);
        _ = CompleteCaptureAiAsync(result.CaptureId, result.ItemId, pending.Hash);
        if (Dispatcher.CheckAccess())
        {
            await RefreshAsync();
        }
        else
        {
            await Dispatcher.InvokeAsync(() => RefreshAsync()).Task.Unwrap();
        }
        return result;
    }

    private async Task CompleteCaptureAiAsync(Guid captureId, long itemId, string hash)
    {
        try
        {
            await _aiWorker.EnqueueAndRunAsync(itemId, hash);
            var candidate = (await _repository.GetMetadataCandidatesAsync(itemId))
                .FirstOrDefault(value =>
                    value.Status == MetadataCandidateStatus.Pending
                    && value.FieldType == "description");
            if (candidate is not null)
            {
                await _clipboard.ShowAiSummaryAsync(captureId, candidate.Value);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ai-capture", "Capture AI draft could not be displayed.", ex);
        }
    }

    private async Task RefreshAfterCaptureChangeAsync()
    {
        if (Dispatcher.CheckAccess())
        {
            await RefreshAsync();
        }
        else
        {
            await Dispatcher.InvokeAsync(() => RefreshAsync()).Task.Unwrap();
        }
    }

    private void RowsListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _aiWorker.MarkUiActive();
        _frameSampler.BeginInteraction("gallery-scroll", TimeSpan.FromMilliseconds(750));
        ThumbnailPresentationQueue.NotifyHighMotion(TimeSpan.FromMilliseconds(220));
        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        if (_rowsScrollViewer is null) return;

        var notches = e.Delta / 120d;
        var target = _rowsScrollViewer.VerticalOffset - notches * GalleryWheelPixelsPerNotch;
        target = Math.Clamp(target, 0, _rowsScrollViewer.ScrollableHeight);
        _rowsScrollViewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void RowsListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer viewer) _rowsScrollViewer = viewer;
        if (Math.Abs(e.VerticalChange) > 0.01)
        {
            _galleryScrollDirection = e.VerticalChange > 0
                ? GalleryScrollDirection.Forward
                : GalleryScrollDirection.Backward;
            _lastGalleryVerticalOffset = _rowsScrollViewer?.VerticalOffset
                ?? _lastGalleryVerticalOffset + e.VerticalChange;
            _frameSampler.BeginInteraction("gallery-scroll", TimeSpan.FromMilliseconds(750));
            ThumbnailPresentationQueue.NotifyHighMotion(TimeSpan.FromMilliseconds(220));
            _thumbnailIdleTimer.Stop();
            _thumbnailIdleTimer.Start();
            QueueRollingWarmup();
        }
        QueueThumbnailPriorityRefresh();
        QueueNextPageIfNeeded();
    }

    private void StartDevelopmentScrollProbe()
    {
        if (!DevelopmentPerformanceTrace.IsEnabled
            || _developmentScrollProbeRendering is not null)
        {
            return;
        }

        _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
        if (_rowsScrollViewer is null) return;
        var clock = Stopwatch.StartNew();
        var direction = 1d;
        const double pixelsPerFrame = 16d;
        var lastRenderingTime = TimeSpan.Zero;
        _developmentScrollProbeRendering = (_, args) =>
        {
            if (_rowsScrollViewer is null) return;
            if (clock.Elapsed >= TimeSpan.FromSeconds(3))
            {
                if (_developmentScrollProbeRendering is not null)
                {
                    CompositionTarget.Rendering -= _developmentScrollProbeRendering;
                    _developmentScrollProbeRendering = null;
                }
                DevelopmentPerformanceTrace.Event("gallery-scroll-gate-complete", new
                {
                    durationMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3),
                    pixelsPerFrame,
                    loadedItems = _items.Count,
                    totalItems = _totalCount
                });
                return;
            }

            if (args is not RenderingEventArgs rendering
                || rendering.RenderingTime == lastRenderingTime)
            {
                return;
            }
            lastRenderingTime = rendering.RenderingTime;
            if (_rowsScrollViewer.VerticalOffset >= _rowsScrollViewer.ScrollableHeight - 96)
            {
                direction = -1;
            }
            else if (_rowsScrollViewer.VerticalOffset <= 96)
            {
                direction = 1;
            }

            ThumbnailPresentationQueue.NotifyHighMotion(TimeSpan.FromMilliseconds(220));
            var target = Math.Clamp(
                _rowsScrollViewer.VerticalOffset + direction * pixelsPerFrame,
                0,
                _rowsScrollViewer.ScrollableHeight);
            _rowsScrollViewer.ScrollToVerticalOffset(target);
        };
        DevelopmentPerformanceTrace.Event("gallery-scroll-gate-start", new
        {
            pixelsPerFrame,
            loadedItems = _items.Count,
            totalItems = _totalCount
        });
        CompositionTarget.Rendering += _developmentScrollProbeRendering;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_startupRefreshPending) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterRefresh) return;
        if (CategoryList.SelectedItem is null) return;
        if (_externalFolderId is not null)
        {
            _externalFolderId = null;
            _suppressExternalRefresh = true;
            ExternalFolderList.SelectedIndex = -1;
            _suppressExternalRefresh = false;
        }
        if (_showTrash)
        {
            _showTrash = false;
            FinishSelectionOperation();
            UpdateTrashVisual();
        }

        FinishSelectionOperation();
        UpdateLayoutControlVisuals();
        await RefreshAsync(RefreshAnimationKind.ViewSwitch);
    }

    private async void ExternalFolderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressExternalRefresh) return;
        if (ExternalFolderList.SelectedItem is not ExternalFolderChoice choice) return;
        _externalFolderId = choice.Id;
        _favoritesOnly = false;
        _showTrash = false;
        _suppressFilterRefresh = true;
        CategoryList.SelectedIndex = -1;
        _suppressFilterRefresh = false;
        FinishSelectionOperation();
        UpdateLayoutControlVisuals();
        await RefreshAsync(RefreshAnimationKind.ViewSwitch);
    }

    private async void TrashClick(object sender, RoutedEventArgs e)
    {
        _showTrash = !_showTrash;
        _favoritesOnly = false;
        _externalFolderId = null;
        FinishSelectionOperation();
        _suppressExternalRefresh = true;
        ExternalFolderList.SelectedIndex = -1;
        _suppressExternalRefresh = false;
        if (_showTrash && CategoryList.SelectedIndex != 0)
        {
            _suppressFilterRefresh = true;
            CategoryList.SelectedIndex = 0;
            _suppressFilterRefresh = false;
        }

        UpdateTrashVisual();
        UpdateLayoutControlVisuals();
        await RefreshAsync(RefreshAnimationKind.ViewSwitch);
    }

    private void CaptureToggleClick(object sender, RoutedEventArgs e)
    {
        _settings.CaptureListeningEnabled = !_clipboard.IsEnabled;
        _settings.Save();
        _clipboard.SetEnabled(_settings.CaptureListeningEnabled);
        UpdateCaptureToggleVisual();
        UpdateBaseStatus();
        ToastService.Show(this, _clipboard.IsEnabled ? "\u6536\u5F55\u76D1\u542C\u5DF2\u5F00\u542F" : "\u6536\u5F55\u76D1\u542C\u5DF2\u5173\u95ED");
    }

    private async void QuickCaptureModeClick(object sender, RoutedEventArgs e)
    {
        _settings.CaptureQuickEditEnabled = !_settings.CaptureQuickEditEnabled;
        if (_settings.CaptureQuickEditEnabled)
        {
            await EnsureQuickCaptureCategoriesAsync();
            await LoadCategoriesAsync();
        }
        _settings.Save();
        UpdateQuickCaptureModeVisual();
        ToastService.Show(
            this,
            _settings.CaptureQuickEditEnabled
                ? "快速标注已开启：复制图片后直接编辑"
                : "已切回安静收录：先显示状态胶囊");
    }

    private async Task EnsureQuickCaptureCategoriesAsync()
    {
        await _repository.GetOrCreateCategoryAsync(
            "上衣参考",
            "用户人工确认的上衣参考主分类；AI 不得覆盖。");
        await _repository.GetOrCreateCategoryAsync(
            "裤子参考",
            "用户人工确认的裤子参考主分类；AI 不得覆盖。");
    }

    private void MultiSelectClick(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = !_multiSelectMode;
        if (!_multiSelectMode && _selectedItemIds.Count > 1)
        {
            var keep = CurrentSelectionId();
            _selectedItemIds.Clear();
            if (keep is not null) _selectedItemIds.Add(keep.Value);
            _selectionAnchorId = keep;
            _selectionFocusId = keep;
            ApplySelectionState();
        }
        UpdateSelectionVisual();
    }

    private void RestoreInspectorGeometry(MainWindowSnapshot snapshot)
    {
        _inspectorVisible = snapshot.InspectorVisible;
        if (!_inspectorVisible)
        {
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorPanel.IsHitTestVisible = false;
            InspectorSplitter.Visibility = Visibility.Collapsed;
            InspectorSplitterColumn.Width = new GridLength(0);
            InspectorColumn.Width = new GridLength(0);
            return;
        }

        var width = ResponsiveInspectorWidth(snapshot.InspectorWidth);
        InspectorColumn.Width = new GridLength(width, GridUnitType.Pixel);
        InspectorSplitterColumn.Width = new GridLength(6, GridUnitType.Pixel);
        InspectorPanel.Visibility = _transparentMode ? Visibility.Hidden : Visibility.Visible;
        InspectorPanel.IsHitTestVisible = !_transparentMode;
        InspectorPanel.Opacity = _transparentMode ? 0 : 1;
        InspectorSplitter.Visibility = _transparentMode ? Visibility.Hidden : Visibility.Visible;
    }

    private bool RestoreTransferredGallerySession()
    {
        if (_initialSnapshot?.GallerySession is not { } session) return false;
        _transferredSessionRestoreCount++;
        _items.Clear();
        _items.AddRange(session.Items);
        Rows.ReplaceAll(session.Rows);
        _layoutWidth = session.LayoutWidth;
        _totalCount = session.TotalCount;
        _hasMoreItems = session.HasMoreItems;
        _activeSearch = session.ActiveSearch;
        _nextPageCursor = session.NextPageCursor;
        _nextExternalPageCursor = session.NextExternalCursor;
        _isLoadingNextPage = false;
        _fastBrowsePlan = AdaptiveFastBrowsePolicy.Create(
            session.TotalCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _fastBrowseLease = _fastBrowseGenerations.Begin(_loadCancellation.Token);
        InvalidateMasonryLayoutIndex();
        EmptyGalleryState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCountText();
        UpdateBaseStatus();
        UpdateTrashVisual();
        ApplySelectionState();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _rowsScrollViewer ??= FindDescendant<ScrollViewer>(RowsList);
            _rowsScrollViewer?.ScrollToVerticalOffset(session.VerticalOffset);
            QueueThumbnailPriorityRefresh();
        }));
        if (session.FastBrowseIndexing && session.ActiveSearch is not null)
        {
            StartFastBrowseBackground(session.ActiveSearch, _fastBrowseLease);
        }
        else
        {
            QueueNextPageIfNeeded();
        }
        DevelopmentPerformanceTrace.Event("transparent-gallery-session-restored", new
        {
            transparent = _transparentMode,
            items = session.Items.Length,
            rows = session.Rows.Length,
            session.LayoutWidth,
            session.VerticalOffset,
            reusedRealizedCards = session.Rows.Sum(row => row.Items.Count)
        });
        return true;
    }

    private async void DeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (IsExternalMode)
        {
            ShowSubtleStatus("\u5916\u90E8\u6587\u4EF6\u5939\u4E0D\u4F1A\u5220\u9664\u6E90\u6587\u4EF6\uFF0C\u53EF\u53F3\u952E\u6536\u85CF\u5230\u56FE\u5E93");
            return;
        }

        if (_selectedItemIds.Count == 0)
        {
            ToastService.Show(this, "\u5148\u9009\u62E9\u8981\u5904\u7406\u7684\u56FE\u7247");
            return;
        }

        await ApplyTrashActionAsync(_selectedItemIds.ToArray());
    }

    private async void AddCategoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new CategoryDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await _repository.AddCategoryAsync(dialog.CategoryName, dialog.Description); await LoadCategoriesAsync(); }
        catch (Exception ex) { ToastService.Show(this, ex.Message); }
    }

    private async void AddExternalFolderClick(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog { Description = "\u9009\u62E9\u8981\u6D4F\u89C8\u7684\u56FE\u7247\u6587\u4EF6\u5939", UseDescriptionForTitle = true };
        if (picker.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
        var path = System.IO.Path.GetFullPath(picker.SelectedPath);
        var existing = _settings.ExternalFolders.FirstOrDefault(x => string.Equals(System.IO.Path.GetFullPath(x.Path), path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ExternalFolderSetting
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                Path = path,
                AddedAt = DateTimeOffset.UtcNow
            };
            _settings.ExternalFolders.Add(existing);
            _settings.Save();
            _externalIndex.RegisterFolder(existing);
            await LoadExternalFoldersAsync();
        }

        _externalFolderId = existing.Id;
        if (ExternalFolderList.ItemsSource is IEnumerable<ExternalFolderChoice> choices)
        {
            _suppressExternalRefresh = true;
            ExternalFolderList.SelectedItem = choices.FirstOrDefault(x => x.Id == existing.Id);
            _suppressExternalRefresh = false;
        }
        await RefreshAsync();
    }

    private async void ImportModelClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "\u9009\u62E9 FR_Imageprompt AI \u6A21\u578B\u5305", Filter = "\u6A21\u578B\u5305 (*.zip)|*.zip" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            await using var stream = File.OpenRead(picker.FileName);
            var hash = await ContentHasher.Sha256Async(stream);
            await new ModelPackInstaller(retainedBackups: _settings.ModelBackupRetentionCount)
                .VerifyAndInstallAsync(picker.FileName, hash, _repository.Paths.Models);
            ToastService.Show(this, "\u6A21\u578B\u5305\u5DF2\u5BFC\u5165");
        }
        catch (Exception ex) { ToastService.Show(this, $"\u6A21\u578B\u5305\u5BFC\u5165\u5931\u8D25\uFF1A{ex.Message}"); }
    }

    private void OpenAiSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ToastService.Show(
                this,
                _settings.OnlineAiEnabled
                    ? "在线 AI 已启用；后续新收录将同时生成在线草稿"
                    : "在线 AI 保持关闭");
        }
    }

    private void OpenLibraryClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo
    { FileName = _repository.Paths.Root, UseShellExecute = true });

    private void GalleryRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryRow row } element)
        {
            RealizeRow(row, element);
        }
    }

    private void GalleryRowUnloaded(object sender, RoutedEventArgs e)
    {
        if (_galleryRowsTransferredOut) return;
        if (sender is not FrameworkElement element
            || element.DataContext is not GalleryRow row)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            // Recycling can raise DataContextChanged and Unloaded in the same
            // layout pass, then reattach the same element for the new row.
            // Releasing synchronously here clears the newly realized row and
            // leaves an empty card until a later scroll. Only release after
            // the recycling pass has settled and the element is still detached.
            if (element.IsLoaded || !ReferenceEquals(element.DataContext, row)) return;
            if (_realizedRowElements.TryGetValue(row, out var tracked)
                && ReferenceEquals(tracked, element))
            {
                ReleaseRow(row);
            }
        }));
    }

    private void GalleryRowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_galleryRowsTransferredOut && e.OldValue is GalleryRow oldRow) ReleaseRow(oldRow);
        if (sender is FrameworkElement { IsLoaded: true } element && e.NewValue is GalleryRow newRow)
        {
            RealizeRow(newRow, element);
        }
    }

    private void RealizeRow(GalleryRow row, FrameworkElement element)
    {
        _realizedRowElements[row] = element;
        if (!row.IsRealized)
        {
            row.Realize(_repository.Paths, id => _selectedItemIds.Contains(id));
        }
        QueueThumbnailPriorityRefresh();
    }

    private void QueueThumbnailPriorityRefresh()
    {
        if (_thumbnailPriorityRefreshQueued) return;
        _thumbnailPriorityRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(UpdateThumbnailPriorities));
    }

    private void UpdateThumbnailPriorities()
    {
        _thumbnailPriorityRefreshQueued = false;
        var viewportBottom = RowsList.ActualHeight;
        if (viewportBottom <= 0) return;
        ReleaseRowsOutsideMasonryCache();

        foreach (var (row, element) in _realizedRowElements.ToArray())
        {
            if (!row.IsRealized || !element.IsLoaded) continue;
            double rowTop;
            try
            {
                rowTop = element.TranslatePoint(new Point(0, 0), RowsList).Y;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var rowBottom = rowTop + Math.Max(element.ActualHeight, row.RowHeight);
            var priority = rowBottom > 0 && rowTop < viewportBottom
                ? ThumbnailRequestPriority.Visible
                : ThumbnailRequestPriority.Prefetch;
            var dpiScale = VisualTreeHelper.GetDpi(element).DpiScaleX;
            foreach (var card in row.Items)
            {
                _ = card.LoadMotionAsync(
                    priority,
                    _fastBrowsePlan.MotionPixels);
            }
        }
    }

    private void UpgradeVisibleThumbnailsAfterIdle()
    {
        var viewportBottom = RowsList.ActualHeight;
        if (viewportBottom <= 0) return;
        var upgraded = 0;
        foreach (var (row, element) in _realizedRowElements.ToArray())
        {
            if (!row.IsRealized || !element.IsLoaded) continue;
            double rowTop;
            try
            {
                rowTop = element.TranslatePoint(new Point(0, 0), RowsList).Y;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            var rowBottom = rowTop + Math.Max(element.ActualHeight, row.RowHeight);
            if (rowBottom <= 0 || rowTop >= viewportBottom) continue;
            var dpiScale = VisualTreeHelper.GetDpi(element).DpiScaleX;
            foreach (var card in row.Items)
            {
                upgraded++;
                _ = card.LoadHighQualityAsync(
                    ThumbnailRequestPriority.Visible,
                    dpiScale);
            }
        }
        DevelopmentPerformanceTrace.Event("thumbnail-idle-upgrade", new
        {
            delayMs = _fastBrowsePlan.IdleHighQualityDelay.TotalMilliseconds,
            direction = _galleryScrollDirection.ToString(),
            verticalOffset = Math.Round(_lastGalleryVerticalOffset, 3),
            requestedCards = upgraded,
            highMotion = ThumbnailPresentationQueue.IsHighMotion
        });
    }

    private void ReleaseRowsOutsideMasonryCache()
    {
        var panel = FindDescendant<VirtualizingMasonryPanel>(RowsList);
        if (panel is null || _realizedRowElements.Count == 0) return;

        var retainedRows = panel.GetRealizedItemIndices()
            .Where(index => index >= 0 && index < Rows.Count)
            .Select(index => Rows[index])
            .ToHashSet();
        foreach (var row in _realizedRowElements.Keys.ToArray())
        {
            if (!retainedRows.Contains(row)) ReleaseRow(row);
        }
    }

    private static void AnimateScale(Border? border, double to)
    {
        if (border?.RenderTransform is not ScaleTransform transform) return;
        if (transform.IsFrozen) { transform = transform.Clone(); border.RenderTransform = transform; }
        var animation = new DoubleAnimation(to, VisualModeService.Motion(MotionToken.Micro)) { EasingFunction = new QuadraticEase() };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void AnimateDragLift(FrameworkElement? element, bool active)
    {
        if (element is null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 0.72 : 1d, VisualModeService.Motion(MotionToken.Fast)) { EasingFunction = ease });
        if (element is Border border) AnimateScale(border, active ? 0.985 : 1d);
    }

    private void CardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement cardElement
            || cardElement.DataContext is not GalleryCardViewModel card)
        {
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        FocusGalleryInput();
        if (e.ClickCount == 2)
        {
            ShowImmersiveViewer(card.Id);
            e.Handled = true;
            return;
        }

        _dragCandidate = card;
        _dragStart = e.GetPosition(this);
    }

    private void CardRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (!GalleryViewportPolicy.ShouldBringIntoView(GalleryBringIntoViewIntent.PointerCardInteraction))
        {
            e.Handled = true;
        }
    }

    private void CardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < 8 && Math.Abs(point.Y - _dragStart.Y) < 8) return;
        StartCardDrag(sender as DependencyObject, _dragCandidate);
    }

    private void StartCardDrag(DependencyObject? source, GalleryCardViewModel card)
    {
        if (source is null) return;
        var paths = GetOperationTargetEntries(card)
            .Select(ResolveOriginalPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _dragCandidate = null;
        if (paths.Length == 0) return;
        _ignoreNextCardClick = true;
        var element = source as FrameworkElement;
        var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, paths);
        data.SetData(InternalCardDragFormat, true, false);
        var boardIds = GetOperationTargetEntries(card)
            .Where(entry => !entry.IsExternal && entry.DeletedAt is null)
            .Select(entry => entry.Id)
            .Distinct()
            .ToArray();
        if (boardIds.Length > 0) data.SetData(BoardWindow.BoardItemIdsDragFormat, boardIds, false);
        AnimateDragLift(element, true);
        try
        {
            System.Windows.DragDrop.DoDragDrop(source, data, System.Windows.DragDropEffects.Copy);
        }
        finally
        {
            AnimateDragLift(element, false);
        }
    }

    private void CardClick(object sender, MouseButtonEventArgs e)
    {
        if (_ignoreNextCardClick)
        {
            _ignoreNextCardClick = false;
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is not GalleryCardViewModel card) return;
        if (e.ClickCount > 1) return;
        HandleCardSelection(card);
        FocusGalleryInput();
    }

    private void ClearSelection()
    {
        _selectedItemIds.Clear();
        _selectionAnchorId = null;
        _selectionFocusId = null;
        ApplySelectionState();
    }

    private void FinishSelectionOperation()
    {
        _multiSelectMode = false;
        ClearSelection();
    }

    private void UpdateSelectionVisual()
    {
        if (MultiSelectButton is null || DeleteSelectedButton is null) return;
        MultiSelectButton.Content = _multiSelectMode ? "\u9000\u51FA\u591A\u9009" : "\u591A\u9009";
        MultiSelectButton.Background = _multiSelectMode ? new SolidColorBrush(Color.FromRgb(62, 118, 153)) : new SolidColorBrush(Color.FromRgb(29, 42, 59));
        DeleteSelectedButton.Content = _showTrash ? $"\u6062\u590D\u9009\u4E2D{SelectedSuffix()}" : $"\u5220\u9664\u9009\u4E2D{SelectedSuffix()}";
        DeleteSelectedButton.IsEnabled = !IsExternalMode && _selectedItemIds.Count > 0;
    }

    private string SelectedSuffix() => _selectedItemIds.Count > 0 ? $" - {_selectedItemIds.Count}" : "";

    private void UpdateTrashVisual()
    {
        if (TrashButton is null) return;
        TrashButton.Content = _showTrash ? "\u9000\u51FA\u56DE\u6536\u7AD9" : "\u56DE\u6536\u7AD9";
        TrashButton.Background = _showTrash ? new SolidColorBrush(Color.FromRgb(62, 118, 153)) : new SolidColorBrush(Color.FromRgb(29, 42, 59));
        if (DeleteSelectedButton is not null) DeleteSelectedButton.Content = _showTrash ? $"\u6062\u590D\u9009\u4E2D{SelectedSuffix()}" : $"\u5220\u9664\u9009\u4E2D{SelectedSuffix()}";
    }

    private void UpdateCaptureToggleVisual()
    {
        if (CaptureToggleButton is null) return;
        CaptureToggleButton.Content = _clipboard.IsEnabled ? "\u6536\u5F55\u76D1\u542C\uFF1A\u5F00" : "\u6536\u5F55\u76D1\u542C\uFF1A\u5173";
        CaptureToggleButton.Background = _clipboard.IsEnabled ? new SolidColorBrush(Color.FromRgb(29, 42, 59)) : new SolidColorBrush(Color.FromRgb(49, 58, 72));
        CaptureToggleButton.BorderBrush = _clipboard.IsEnabled ? new SolidColorBrush(Color.FromRgb(86, 214, 255)) : new SolidColorBrush(Color.FromRgb(73, 88, 106));
    }

    private void UpdateQuickCaptureModeVisual()
    {
        if (QuickCaptureModeButton is null) return;
        QuickCaptureModeButton.Content = _settings.CaptureQuickEditEnabled
            ? "收录：快速"
            : "收录：安静";
        QuickCaptureModeButton.Background = _settings.CaptureQuickEditEnabled
            ? new SolidColorBrush(Color.FromRgb(62, 118, 153))
            : new SolidColorBrush(Color.FromRgb(29, 42, 59));
    }

    private void UpdateBaseStatus()
    {
        if (StatusText is null) return;
        if (IsExternalMode
            && _externalFolderId is not null
            && _externalFolderStates.TryGetValue(_externalFolderId, out var externalState)
            && externalState.Status != ExternalFolderIndexStatus.Ready)
        {
            StatusText.Text = externalState.Status switch
            {
                ExternalFolderIndexStatus.Indexing =>
                    $"正在后台建立外部文件夹索引，已发现 {externalState.AvailableFiles:N0} 张图片…",
                ExternalFolderIndexStatus.Missing =>
                    "外部文件夹不存在、磁盘未连接或路径已经移动。",
                ExternalFolderIndexStatus.PermissionDenied =>
                    "FR_Imageprompt 没有读取这个外部文件夹的权限。",
                ExternalFolderIndexStatus.Failed =>
                    externalState.LastError ?? "外部文件夹索引失败。",
                _ => "外部文件夹正在等待建立索引。"
            };
            return;
        }
        if (!IsExternalMode
            && !string.IsNullOrWhiteSpace(SearchBox.Text)
            && !_repository.FullTextSearchAvailable)
        {
            StatusText.Text = "\u5168\u6587\u7D22\u5F15\u6682\u4E0D\u53EF\u7528\uFF0C\u5DF2\u5207\u6362\u517C\u5BB9\u641C\u7D22\uFF08\u7ED3\u679C\u5B8C\u6574\uFF0C\u901F\u5EA6\u53EF\u80FD\u7A0D\u6162\uFF09";
            return;
        }

        StatusText.Text = IsExternalMode
            ? "\u5916\u90E8\u6587\u4EF6\u5939\uFF1A\u53EF\u62D6\u51FA\u56FE\u7247\uFF0C\u53F3\u952E\u6216 Alt+M \u6536\u85CF\u5230\u56FE\u5E93"
            : (_showTrash
                ? "\u8BB0\u5F55\u5C06\u5728\u79FB\u5165\u56DE\u6536\u7AD9 30 \u5929\u540E\u81EA\u52A8\u6E05\u7406"
                : _favoritesOnly
                    ? "正在显示已收藏图片"
                    : (_clipboard.IsEnabled ? "\u590D\u5236\u6216\u62D6\u5165\u4E00\u5F20\u56FE\u7247\u5373\u53EF\u5F00\u59CB\u6536\u5F55" : "\u6536\u5F55\u76D1\u542C\u5DF2\u5173\u95ED\uFF0C\u53EF\u6B63\u5E38\u6D4F\u89C8\u56FE\u7247\u4E0E\u590D\u5236\u63D0\u793A\u8BCD"));
    }

    private void ShowSubtleStatus(string message)
    {
        StatusText.Text = message;
        _subtleStatusTimer.Stop();
        _subtleStatusTimer.Start();
    }

    private void CardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border { ContextMenu: { } menu } border || border.DataContext is not GalleryCardViewModel card)
        {
            e.Handled = true;
            return;
        }

        var targets = GetOperationTargetEntries(card).ToArray();
        if (targets.Length == 0)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        menu.Background = new SolidColorBrush(Color.FromArgb(245, 17, 28, 42));
        menu.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        var details = new System.Windows.Controls.MenuItem
        {
            Header = "查看详情",
            Tag = card.Id
        };
        details.Click += OpenCardDetailsClick;
        menu.Items.Add(details);
        menu.Items.Add(new Separator());
        if (targets.Length > 1)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = $"\u5DF2\u9009 {targets.Length} \u5F20", IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        if (targets.Any(x => x.IsExternal))
        {
            var collect = new System.Windows.Controls.MenuItem { Header = "\u6536\u85CF\u5230\u56FE\u5E93", Tag = new ExternalCollect(targets, null) };
            collect.Click += CollectExternalClick;
            menu.Items.Add(collect);
            var collectTo = new System.Windows.Controls.MenuItem { Header = "\u6536\u85CF\u5230\u5206\u7C7B" };
            AddExternalCollectCategoryItem(collectTo, targets, null, "\u672A\u5206\u7C7B");
            foreach (var category in _categories) AddExternalCollectCategoryItem(collectTo, targets, category.Id, category.Name);
            menu.Items.Add(collectTo);
            return;
        }

        var ids = targets.Select(x => x.Id).ToArray();
        if (!_showTrash)
        {
            var edit = new System.Windows.Controls.MenuItem
            {
                Header = targets.Length > 1 ? "\u6279\u91CF\u4FEE\u6539\u6807\u7B7E\u548C\u5907\u6CE8" : "\u4FEE\u6539\u6807\u7B7E\u548C\u5907\u6CE8",
                Tag = targets
            };
            edit.Click += EditMetadataClick;
            menu.Items.Add(edit);
            menu.Items.Add(new Separator());

            var move = new System.Windows.Controls.MenuItem { Header = "\u79FB\u52A8\u5230\u5206\u7C7B" };
            AddMoveCategoryItem(move, ids, null, "\u672A\u5206\u7C7B");
            foreach (var category in _categories) AddMoveCategoryItem(move, ids, category.Id, category.Name);
            menu.Items.Add(move);
            menu.Items.Add(new Separator());
        }

        var trash = new System.Windows.Controls.MenuItem { Header = _showTrash ? "\u4ECE\u56DE\u6536\u7AD9\u6062\u590D" : "\u79FB\u5230\u56DE\u6536\u7AD9", Tag = ids };
        trash.Click += DeleteCardClick;
        menu.Items.Add(trash);
        if (_showTrash)
        {
            menu.Items.Add(new Separator());
            var permanentDelete = new System.Windows.Controls.MenuItem { Header = "\u6C38\u4E45\u5220\u9664", Tag = ids };
            permanentDelete.Click += PermanentlyDeleteCardClick;
            menu.Items.Add(permanentDelete);
        }
    }

    private async void DeleteCardClick(object sender, RoutedEventArgs e)
    {
        var ids = (sender as FrameworkElement)?.Tag as long[];
        if (ids is null || ids.Length == 0)
        {
            var card = GetCardFromMenuSender(sender);
            ids = GetOperationTargetEntries(card).Where(x => !x.IsExternal).Select(x => x.Id).ToArray();
        }

        await ApplyTrashActionAsync(ids);
    }

    private async void PermanentlyDeleteCardClick(object sender, RoutedEventArgs e)
    {
        var ids = (sender as FrameworkElement)?.Tag as long[];
        if (ids is null || ids.Length == 0)
        {
            var card = GetCardFromMenuSender(sender);
            ids = GetOperationTargetEntries(card).Where(x => !x.IsExternal).Select(x => x.Id).ToArray();
        }

        await PermanentlyDeleteTrashItemsAsync(ids);
    }

    private async Task PermanentlyDeleteTrashItemsAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        var dialog = new PermanentDeleteDialog(ids.Count) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        await _repository.PermanentlyDeleteTrashItemsAsync(ids);
        ToastService.Show(this, $"\u5DF2\u6C38\u4E45\u5220\u9664 {ids.Count} \u5F20\u56FE\u7247");
        FinishSelectionOperation();
        await RefreshAsync();
    }
    private async Task ApplyTrashActionAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        foreach (var id in ids)
        {
            if (_showTrash) await _repository.RestoreAsync(id);
            else await _repository.MoveToTrashAsync(id);
        }

        ToastService.Show(this, _showTrash ? $"\u5DF2\u6062\u590D {ids.Count} \u5F20\u56FE\u7247" : $"\u5DF2\u79FB\u5165\u56DE\u6536\u7AD9 {ids.Count} \u5F20\u56FE\u7247");
        FinishSelectionOperation();
        await RefreshAsync();
    }

    private async void EditMetadataClick(object sender, RoutedEventArgs e)
    {
        var targets = (sender as FrameworkElement)?.Tag as GalleryEntry[];
        if (targets is null || targets.Length == 0)
        {
            var card = GetCardFromMenuSender(sender);
            targets = GetOperationTargetEntries(card).Where(x => !x.IsExternal).ToArray();
        }

        targets = targets.Where(x => !x.IsExternal).ToArray();
        if (targets.Length == 0) return;
        targets = await EnsureFullEntriesAsync(targets);
        if (targets.Length == 0) return;

        var dialog = new EditMetadataDialog(
            targets.Length,
            CommonText(targets.Select(x => x.Tags)),
            CommonText(targets.Select(x => x.Notes)),
            targets.Length > 1)
        { Owner = this };
        if (dialog.ShowDialog() != true) return;

        await _repository.UpdateItemsMetadataAsync(
            targets.Select(x => x.Id),
            dialog.ApplyTags ? dialog.Tags : null,
            dialog.ApplyNotes ? dialog.Notes : null);

        ToastService.Show(this, targets.Length > 1 ? $"\u5DF2\u6279\u91CF\u4FEE\u6539 {targets.Length} \u5F20" : "\u6807\u7B7E\u548C\u5907\u6CE8\u5DF2\u4FDD\u5B58");
        FinishSelectionOperation();
        await RefreshAsync();
    }

    private static string CommonText(IEnumerable<string> values)
    {
        var list = values.ToArray();
        if (list.Length == 0) return string.Empty;
        var first = list[0];
        return list.All(x => string.Equals(x, first, StringComparison.Ordinal)) ? first : string.Empty;
    }

    private void AddMoveCategoryItem(System.Windows.Controls.MenuItem parent, long[] ids, long? categoryId, string name)
    {
        var item = new System.Windows.Controls.MenuItem { Header = name, Tag = new CategoryMove(ids, categoryId, name) };
        item.Click += MoveToCategoryClick;
        parent.Items.Add(item);
    }

    private async void MoveToCategoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryMove move) return;
        await _repository.UpdateItemsCategoryAsync(move.ItemIds, move.CategoryId);
        ToastService.Show(this, $"\u5DF2\u79FB\u52A8 {move.ItemIds.Length} \u5F20\u5230 {move.Name}");
        FinishSelectionOperation();
        await RefreshAsync();
    }

    private void AddExternalCollectCategoryItem(System.Windows.Controls.MenuItem parent, GalleryEntry[] entries, long? categoryId, string name)
    {
        var item = new System.Windows.Controls.MenuItem { Header = name, Tag = new ExternalCollect(entries, categoryId) };
        item.Click += CollectExternalClick;
        parent.Items.Add(item);
    }

    private async void CollectExternalClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExternalCollect collect) return;
        await CollectExternalEntriesAsync(collect.Entries, collect.CategoryId);
    }

    private async Task CollectExternalEntriesAsync(IReadOnlyList<GalleryEntry> entries, long? categoryId)
    {
        var external = entries.Where(x => x.IsExternal && File.Exists(x.OriginalPath)).ToArray();
        if (external.Length == 0)
        {
            ShowSubtleStatus("\u6CA1\u6709\u53EF\u6536\u85CF\u7684\u5916\u90E8\u56FE\u7247");
            return;
        }

        var saved = 0;
        var duplicates = 0;
        var failed = 0;
        foreach (var entry in external)
        {
            try
            {
                var pending = await _capture.CreateFromFileAsync(entry.OriginalPath);
                var result = await _capture.SaveAsync(pending, entry.Prompt, entry.Notes, categoryId, []);
                if (result.WasDuplicate) duplicates++; else saved++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warning("external-collect", "External image could not be collected.", ex);
            }
        }

        FinishSelectionOperation();
        var message = duplicates > 0 && saved == 0
            ? $"\u5DF2\u5728\u56FE\u5E93\u4E2D - {duplicates} \u5F20"
            : duplicates > 0
                ? $"\u5DF2\u6536\u85CF {saved} \u5F20\uFF0C\u5DF2\u5B58\u5728 {duplicates} \u5F20"
                : $"\u5DF2\u6536\u85CF {saved} \u5F20";
        if (failed > 0) message += $"，失败 {failed} 张（已写入诊断日志）";
        ShowSubtleStatus(message);
    }

    private async Task CollectCurrentExternalSelectionAsync()
    {
        if (!IsExternalMode) return;
        GalleryEntry[] targets;
        if (_viewerIndex >= 0 && _viewerIndex < _items.Count && _items[_viewerIndex].IsExternal)
            targets = [_items[_viewerIndex]];
        else
            targets = _selectedItemIds.Count > 0 ? _items.Where(x => _selectedItemIds.Contains(x.Id)).ToArray() : [];
        await CollectExternalEntriesAsync(targets, null);
    }

    private IEnumerable<GalleryEntry> GetOperationTargetEntries(GalleryCardViewModel? card)
    {
        if (card is not null && _selectedItemIds.Contains(card.Id)) return _items.Where(x => _selectedItemIds.Contains(x.Id)).ToArray();
        if (card is null && _selectedItemIds.Count > 0) return _items.Where(x => _selectedItemIds.Contains(x.Id)).ToArray();
        return card is null ? [] : [card.Item];
    }

    private string ResolveOriginalPath(GalleryEntry entry) => entry.IsExternal ? entry.OriginalPath : _repository.Paths.ToAbsolute(entry.OriginalPath);

    private static GalleryCardViewModel? GetCardFromMenuSender(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is GalleryCardViewModel direct) return direct;
        if (sender is System.Windows.Controls.MenuItem { Parent: System.Windows.Controls.ContextMenu { DataContext: GalleryCardViewModel fromMenu } }) return fromMenu;
        return null;
    }


    private void SidebarListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item) return;
        item.IsSelected = true;
        e.Handled = false;
    }

    private void CategoryListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (CategoryList.ContextMenu is not { } menu || CategoryList.SelectedItem is not CategoryChoice { Id: > 0 } choice)
        {
            e.Handled = true;
            return;
        }

        HoldLeftPanelForMenu(menu);
        menu.Items.Clear();
        menu.Background = new SolidColorBrush(Color.FromArgb(245, 17, 28, 42));
        menu.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        var categoryIndex = _categories.ToList().FindIndex(x => x.Id == choice.Id.Value);
        AddSidebarMenuItem(menu, "\u4E0A\u79FB", categoryIndex > 0, async () => await MoveCategoryAsync(choice.Id.Value, -1));
        AddSidebarMenuItem(menu, "\u4E0B\u79FB", categoryIndex >= 0 && categoryIndex < _categories.Count - 1, async () => await MoveCategoryAsync(choice.Id.Value, 1));
        menu.Items.Add(new Separator());
        AddSidebarMenuItem(menu, "\u5220\u9664\u5206\u7C7B", true, async () => await DeleteCategoryFromMenuAsync(choice.Id.Value, choice.Name));
    }

    private async Task MoveCategoryAsync(long categoryId, int direction)
    {
        await _repository.MoveCategoryAsync(categoryId, direction);
        await LoadCategoriesAsync();
        if (CategoryList.ItemsSource is IEnumerable<CategoryChoice> choices)
            CategoryList.SelectedItem = choices.FirstOrDefault(x => x.Id == categoryId);
        await RefreshAsync();
    }

    private async Task DeleteCategoryFromMenuAsync(long categoryId, string name)
    {
        var confirm = MessageBox.Show(this, $"\u5220\u9664\u5206\u7C7B\u201C{name}\u201D\uFF1F\u8FD9\u4E2A\u5206\u7C7B\u91CC\u7684\u56FE\u7247\u4F1A\u4FDD\u7559\uFF0C\u5E76\u79FB\u52A8\u5230\u672A\u5206\u7C7B\u3002", "\u5220\u9664\u5206\u7C7B", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        await _repository.DeleteCategoryAsync(categoryId);
        await LoadCategoriesAsync();
        await RefreshAsync();
        ToastService.Show(this, "\u5206\u7C7B\u5DF2\u5220\u9664\uFF0C\u56FE\u7247\u5DF2\u79FB\u5230\u672A\u5206\u7C7B");
    }

    private void ExternalFolderListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ExternalFolderList.ContextMenu is not { } menu || ExternalFolderList.SelectedItem is not ExternalFolderChoice choice)
        {
            e.Handled = true;
            return;
        }

        HoldLeftPanelForMenu(menu);
        menu.Items.Clear();
        menu.Background = new SolidColorBrush(Color.FromArgb(245, 17, 28, 42));
        menu.Foreground = new SolidColorBrush(Color.FromRgb(238, 246, 255));
        var index = _settings.ExternalFolders.FindIndex(x => x.Id == choice.Id);
        AddSidebarMenuItem(menu, "\u4E0A\u79FB", index > 0, async () => await MoveExternalFolderAsync(choice.Id, -1));
        AddSidebarMenuItem(menu, "\u4E0B\u79FB", index >= 0 && index < _settings.ExternalFolders.Count - 1, async () => await MoveExternalFolderAsync(choice.Id, 1));
        menu.Items.Add(new Separator());
        AddSidebarMenuItem(menu, "\u79FB\u9664\u6587\u4EF6\u5939", true, async () => await RemoveExternalFolderAsync(choice.Id));
    }

    private async Task MoveExternalFolderAsync(string id, int direction)
    {
        var index = _settings.ExternalFolders.FindIndex(x => x.Id == id);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= _settings.ExternalFolders.Count) return;
        (_settings.ExternalFolders[index], _settings.ExternalFolders[target]) = (_settings.ExternalFolders[target], _settings.ExternalFolders[index]);
        _settings.Save();
        await LoadExternalFoldersAsync();
        await RefreshAsync();
    }

    private async Task RemoveExternalFolderAsync(string id)
    {
        var removed = _settings.ExternalFolders.RemoveAll(x => x.Id == id) > 0;
        if (!removed) return;
        _settings.Save();
        await _externalIndex.RemoveFolderAsync(id);
        var wasSelected = _externalFolderId == id;
        if (wasSelected) _externalFolderId = null;
        await LoadExternalFoldersAsync();
        if (wasSelected)
        {
            _suppressFilterRefresh = true;
            CategoryList.SelectedIndex = 0;
            _suppressFilterRefresh = false;
        }
        await RefreshAsync();
        ToastService.Show(this, "\u5916\u90E8\u6587\u4EF6\u5939\u5DF2\u4ECE\u4FA7\u680F\u79FB\u9664\uFF0C\u78C1\u76D8\u6587\u4EF6\u672A\u5220\u9664");
    }

    private static void AddSidebarMenuItem(System.Windows.Controls.ContextMenu menu, string header, bool isEnabled, Func<Task> action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header, IsEnabled = isEnabled };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }


    private static T? FindDescendant<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
    private sealed record GalleryPreparation(
        IReadOnlyDictionary<long, GalleryCardViewModel> ReusableCards,
        IReadOnlyDictionary<long, GalleryCardViewModel> PreparedCards,
        int PreparedRows,
        IReadOnlySet<int>? AdditionalRows = null);

    private sealed record GalleryApplyResult(
        int ReusedRows,
        int ReusedCards,
        int PreparedCards,
        int PreparedRows,
        int FirstViewportCards,
        int ReadyFirstViewportCards);

    private enum RefreshAnimationKind { None, Search, ViewSwitch, ContentChange }

    private sealed record CategoryMove(long[] ItemIds, long? CategoryId, string Name);
    private sealed record ExternalCollect(GalleryEntry[] Entries, long? CategoryId);
    private sealed record CategoryChoice(long? Id, string Name)
    {
        public override string ToString() => Name;
    }
    private sealed record ExternalFolderChoice(string Id, string Name, string Path, string StatusText);
}
