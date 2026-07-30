using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public sealed record GalleryEntry(
    long Id,
    string Hash,
    string OriginalPath,
    string ThumbnailPath,
    string MediumThumbnailPath,
    int Width,
    int Height,
    string Format,
    string Prompt,
    string Notes,
    long? CategoryId,
    string CategoryName,
    string Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt,
    bool IsExternal,
    string? ExternalFolderId,
    bool IsFavorite = false)
{
    public static GalleryEntry FromLibrary(GalleryItem item) => new(
        item.Id,
        item.Hash,
        item.OriginalPath,
        item.ThumbnailPath,
        item.MediumThumbnailPath,
        item.Width,
        item.Height,
        item.Format,
        item.Prompt,
        item.Notes,
        item.CategoryId,
        item.CategoryName,
        item.Tags,
        item.CreatedAt,
        item.DeletedAt,
        false,
        null,
        item.IsFavorite);

    public static GalleryEntry FromExternal(ExternalFileIndexItem item, string rootPath) => new(
        -item.Id,
        $"external:{item.FolderId}:{item.Id}",
        item.Path,
        item.Path,
        item.Path,
        item.Width,
        item.Height,
        item.Format,
        Path.GetFileNameWithoutExtension(item.FileName),
        $"来源文件夹：{rootPath}",
        null,
        "外部文件夹",
        "",
        item.ModifiedAt,
        null,
        true,
        item.FolderId,
        false);

    public GalleryItem ToLibraryItem() => new(
        Id,
        Hash,
        OriginalPath,
        ThumbnailPath,
        MediumThumbnailPath,
        Width,
        Height,
        Format,
        Prompt,
        Notes,
        CategoryId,
        CategoryName,
        Tags,
        CreatedAt,
        DeletedAt,
        IsFavorite);
}

public sealed class GalleryCardViewModel : INotifyPropertyChanged
{
    private sealed record ThumbnailRequestSpec(string Path, int TargetPhysicalPixels);

    private BitmapSource? _thumbnail;
    private bool _thumbnailLoadFailed;
    private bool _isSelected;
    private double _selectionVisualStrength = 1;
    private double _layoutX;
    private double _layoutY;
    private double _layoutWidth;
    private double _imageHeight;
    private ThumbnailRequestPriority _thumbnailPriority = ThumbnailRequestPriority.Prefetch;
    private CancellationTokenSource? _thumbnailLoadCancellation;
    private Task? _thumbnailLoadTask;
    private ThumbnailRequestSpec? _activeThumbnailRequest;
    private ThumbnailRequestSpec? _loadedThumbnailRequest;
    private int _thumbnailLoadGeneration;

    public GalleryCardViewModel(GalleryEntry item, LibraryPaths paths, double layoutWidth, double imageHeight, bool isSelected = false)
        : this(item, paths, 0, 0, layoutWidth, imageHeight, isSelected)
    {
    }

    public GalleryCardViewModel(
        GalleryEntry item,
        LibraryPaths paths,
        double layoutX,
        double layoutY,
        double layoutWidth,
        double imageHeight,
        bool isSelected = false)
    {
        Item = item;
        Paths = paths;
        _layoutX = layoutX;
        _layoutY = layoutY;
        _layoutWidth = layoutWidth;
        _imageHeight = imageHeight;
        _isSelected = isSelected;
    }

    public GalleryEntry Item { get; private set; }
    public LibraryPaths Paths { get; }
    public long Id => Item.Id;
    public bool IsExternal => Item.IsExternal;
    public string CategoryName => Item.CategoryName;
    public string Tags => Item.Tags;
    public double AspectRatio => Item.Height <= 0 ? 1 : Item.Width / (double)Item.Height;
    public double LayoutX { get => _layoutX; private set { if (Math.Abs(_layoutX - value) < 0.1) return; _layoutX = value; OnPropertyChanged(); } }
    public double LayoutY { get => _layoutY; private set { if (Math.Abs(_layoutY - value) < 0.1) return; _layoutY = value; OnPropertyChanged(); } }
    public double LayoutWidth { get => _layoutWidth; private set { if (Math.Abs(_layoutWidth - value) < 0.1) return; _layoutWidth = value; OnPropertyChanged(); } }
    public double ImageHeight { get => _imageHeight; private set { if (Math.Abs(_imageHeight - value) < 0.1) return; _imageHeight = value; OnPropertyChanged(); OnPropertyChanged(nameof(CardHeight)); } }
    public double CardHeight => ImageHeight;
    public BitmapSource? Thumbnail { get => _thumbnail; private set { _thumbnail = value; OnPropertyChanged(); } }
    public bool ThumbnailLoadFailed { get => _thumbnailLoadFailed; private set { if (_thumbnailLoadFailed == value) return; _thumbnailLoadFailed = value; OnPropertyChanged(); } }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionVisualOpacity));
        }
    }
    public double SelectionVisualStrength
    {
        get => _selectionVisualStrength;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(_selectionVisualStrength - value) < 0.01) return;
            _selectionVisualStrength = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionVisualOpacity));
        }
    }
    public double SelectionVisualOpacity => IsSelected ? SelectionVisualStrength : 0;
    public bool IsFavorite
    {
        get => Item.IsFavorite;
        set
        {
            if (Item.IsFavorite == value) return;
            Item = Item with { IsFavorite = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(FavoriteLabel));
        }
    }
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteLabel => IsFavorite ? "取消收藏" : "收藏";
    public string OriginalPath => Item.IsExternal ? Item.OriginalPath : Paths.ToAbsolute(Item.OriginalPath);
    public string ThumbnailPath => Item.IsExternal ? Item.ThumbnailPath : Paths.ToAbsolute(Item.ThumbnailPath);
    public string MediumThumbnailPath => Item.IsExternal ? Item.MediumThumbnailPath : Paths.ToAbsolute(Item.MediumThumbnailPath);

    public void UpdateLayout(double layoutWidth, double imageHeight)
        => UpdateLayout(0, 0, layoutWidth, imageHeight);

    public void UpdateLayout(
        double layoutX,
        double layoutY,
        double layoutWidth,
        double imageHeight)
    {
        LayoutX = layoutX;
        LayoutY = layoutY;
        LayoutWidth = layoutWidth;
        ImageHeight = imageHeight;
    }

    public bool CanReuseFor(GalleryEntry item) =>
        Id == item.Id
        && Item.IsExternal == item.IsExternal
        && string.Equals(Item.Hash, item.Hash, StringComparison.Ordinal)
        && string.Equals(Item.ThumbnailPath, item.ThumbnailPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Item.MediumThumbnailPath, item.MediumThumbnailPath, StringComparison.OrdinalIgnoreCase);

    public void UpdateFrom(
        GalleryEntry item,
        double layoutX,
        double layoutY,
        double layoutWidth,
        double imageHeight,
        bool isSelected)
    {
        if (Id != item.Id)
        {
            throw new InvalidOperationException("Only the same stable gallery item ID can reuse a card view model.");
        }

        var canKeepThumbnail = CanReuseFor(item);
        var itemChanged = Item != item;
        Item = item;
        UpdateLayout(layoutX, layoutY, layoutWidth, imageHeight);
        IsSelected = isSelected;

        if (itemChanged)
        {
            OnPropertyChanged(nameof(Item));
            OnPropertyChanged(nameof(IsExternal));
            OnPropertyChanged(nameof(CategoryName));
            OnPropertyChanged(nameof(Tags));
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(FavoriteLabel));
            OnPropertyChanged(nameof(AspectRatio));
            OnPropertyChanged(nameof(OriginalPath));
            OnPropertyChanged(nameof(ThumbnailPath));
            OnPropertyChanged(nameof(MediumThumbnailPath));
        }

        if (canKeepThumbnail) return;
        CancelThumbnailLoad();
        _loadedThumbnailRequest = null;
        Thumbnail = null;
        ThumbnailLoadFailed = false;
    }

    internal Task LoadAsync(
        ThumbnailRequestPriority priority,
        double dpiScale,
        CancellationToken cancellationToken = default)
        => StartThumbnailLoadAsync(
            priority,
            dpiScale,
            queuePresentation: true,
            cancellationToken);

    internal Task PrepareAsync(
        ThumbnailRequestPriority priority,
        double dpiScale,
        CancellationToken cancellationToken = default)
        => StartThumbnailLoadAsync(
            priority,
            dpiScale,
            queuePresentation: false,
            cancellationToken);

    private Task StartThumbnailLoadAsync(
        ThumbnailRequestPriority priority,
        double dpiScale,
        bool queuePresentation,
        CancellationToken cancellationToken)
    {
        var targetPhysicalPixels = ThumbnailSizingPolicy.SelectTier(
            LayoutWidth,
            ImageHeight,
            dpiScale);
        var path = targetPhysicalPixels <= ThumbnailSizingPolicy.SmallPixels
            ? ThumbnailPath
            : MediumThumbnailPath;
        var request = new ThumbnailRequestSpec(path, targetPhysicalPixels);

        if (_loadedThumbnailRequest == request && _thumbnail is not null)
        {
            return Task.CompletedTask;
        }

        if (_activeThumbnailRequest == request && _thumbnailLoadTask is not null)
        {
            if (priority < _thumbnailPriority)
            {
                _thumbnailPriority = priority;
                ThumbnailCache.Promote(
                    request.Path,
                    request.TargetPhysicalPixels,
                    priority);
            }
            return _thumbnailLoadTask;
        }

        CancelActiveThumbnailLoad();
        _thumbnailPriority = priority;
        _activeThumbnailRequest = request;
        ThumbnailLoadFailed = false;
        var generation = ++_thumbnailLoadGeneration;
        var cancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        _thumbnailLoadCancellation = cancellation;
        _thumbnailLoadTask = LoadThumbnailCoreAsync(
            request,
            cancellation,
            generation,
            queuePresentation);
        return _thumbnailLoadTask;
    }

    public void CancelThumbnailLoad()
    {
        CancelActiveThumbnailLoad();
        _thumbnailLoadGeneration++;
    }

    private void CancelActiveThumbnailLoad()
    {
        _thumbnailLoadCancellation?.Cancel();
        _thumbnailLoadCancellation = null;
        _thumbnailLoadTask = null;
        _activeThumbnailRequest = null;
    }

    private async Task LoadThumbnailCoreAsync(
        ThumbnailRequestSpec request,
        CancellationTokenSource cancellation,
        int generation,
        bool queuePresentation)
    {
        try
        {
            var image = await ThumbnailCache.LoadAsync(
                request.Path,
                request.TargetPhysicalPixels,
                _thumbnailPriority,
                cancellation.Token).ConfigureAwait(false);
            if (!queuePresentation)
            {
                if (!cancellation.IsCancellationRequested && generation == _thumbnailLoadGeneration)
                {
                    _thumbnail = image;
                    _loadedThumbnailRequest = request;
                    _thumbnailLoadFailed = false;
                    CompleteThumbnailLoad(cancellation, generation);
                }
            }
            else
            {
                await ThumbnailPresentationQueue.PresentAsync(() =>
                {
                    if (!cancellation.IsCancellationRequested && generation == _thumbnailLoadGeneration)
                    {
                        Thumbnail = image;
                        _loadedThumbnailRequest = request;
                        ThumbnailLoadFailed = false;
                        CompleteThumbnailLoad(cancellation, generation);
                    }
                }, _thumbnailPriority, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DevelopmentPerformanceTrace.Event("thumbnail-card-load-failed", new
            {
                itemId = Id,
                exception = ex.GetType().Name
            });
            if (!queuePresentation)
            {
                if (generation == _thumbnailLoadGeneration)
                {
                    if (_thumbnail is null) _thumbnailLoadFailed = true;
                    CompleteThumbnailLoad(cancellation, generation);
                }
            }
            else
            {
                await ThumbnailPresentationQueue.PresentAsync(() =>
                {
                    if (generation != _thumbnailLoadGeneration) return;
                    if (Thumbnail is null) ThumbnailLoadFailed = true;
                    CompleteThumbnailLoad(cancellation, generation);
                }, _thumbnailPriority).ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CompleteThumbnailLoad(
        CancellationTokenSource cancellation,
        int generation)
    {
        if (generation != _thumbnailLoadGeneration
            || !ReferenceEquals(_thumbnailLoadCancellation, cancellation))
        {
            return;
        }

        _thumbnailLoadCancellation = null;
        _thumbnailLoadTask = null;
        _activeThumbnailRequest = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record GalleryCardLayout(
    GalleryEntry Item,
    double LayoutX,
    double LayoutY,
    double LayoutWidth,
    double ImageHeight);

public sealed record GalleryItemGeometry(
    GalleryEntry Item,
    int SourceIndex,
    int ColumnIndex,
    double X,
    double Y,
    double Width,
    double Height,
    int LayoutVersion);

public sealed record GalleryLayoutState(
    double AvailableWidth,
    double TargetSize,
    int ColumnCount,
    double HorizontalSpacing,
    double VerticalSpacing,
    IReadOnlyList<double> ColumnHeights,
    int LaidOutItemCount,
    int LayoutVersion);

public sealed record GalleryLayoutAppend(
    bool ReplaceIncompleteTail,
    IReadOnlyList<GalleryRow> Rows);

public sealed class GalleryRow : INotifyPropertyChanged
{
    private IReadOnlyList<GalleryCardLayout> _layoutItems;
    private IReadOnlyList<GalleryCardViewModel> _items = [];
    private double _bottomSpacing;
    private double _panelX;
    private double _panelY;
    private double _panelWidth;

    public GalleryRow(
        IReadOnlyList<GalleryCardLayout> layoutItems,
        bool isFilled,
        double bottomSpacing = GalleryLayoutEngine.DefaultSpacing,
        double panelX = 0,
        double panelY = 0,
        double panelWidth = double.NaN,
        int sourceIndex = 0,
        int columnIndex = -1,
        int layoutVersion = 0)
    {
        _layoutItems = layoutItems;
        IsFilled = isFilled;
        _bottomSpacing = bottomSpacing;
        _panelX = panelX;
        _panelY = panelY;
        _panelWidth = double.IsFinite(panelWidth) && panelWidth >= 0
            ? panelWidth
            : RowContentWidth(layoutItems);
        SourceIndex = sourceIndex;
        ColumnIndex = columnIndex;
        LayoutVersion = layoutVersion;
    }

    public IReadOnlyList<GalleryCardLayout> LayoutItems => _layoutItems;
    public IReadOnlyList<GalleryCardViewModel> Items
    {
        get => _items;
        private set
        {
            if (ReferenceEquals(_items, value)) return;
            _items = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRealized));
        }
    }

    public bool IsFilled { get; private set; }
    public bool IsRealized => Items.Count > 0;
    public double PanelX => _panelX;
    public double PanelY => _panelY;
    public double PanelWidth => _panelWidth;
    public double PanelBottom => PanelY + RowHeight;
    public int SourceIndex { get; private set; }
    public int ColumnIndex { get; private set; }
    public int LayoutVersion { get; private set; }
    public double RowHeight => _layoutItems.Count == 0
        ? 0
        : _layoutItems.Max(item => item.LayoutY + item.ImageHeight);
    public Thickness RowMargin => new(0, 0, 0, _bottomSpacing);

    public void Realize(
        LibraryPaths paths,
        Func<long, bool> isSelected,
        IReadOnlyDictionary<long, GalleryCardViewModel>? reusableCards = null,
        ISet<GalleryCardViewModel>? transferredCards = null)
    {
        if (IsRealized || _layoutItems.Count == 0) return;
        Items = _layoutItems
            .Select(layout =>
            {
                if (reusableCards is not null
                    && reusableCards.TryGetValue(layout.Item.Id, out var reusable)
                    && reusable.CanReuseFor(layout.Item))
                {
                    reusable.UpdateFrom(
                        layout.Item,
                        layout.LayoutX,
                        layout.LayoutY,
                        layout.LayoutWidth,
                        layout.ImageHeight,
                        isSelected(layout.Item.Id));
                    transferredCards?.Add(reusable);
                    return reusable;
                }

                return new GalleryCardViewModel(
                    layout.Item,
                    paths,
                    layout.LayoutX,
                    layout.LayoutY,
                    layout.LayoutWidth,
                    layout.ImageHeight,
                    isSelected(layout.Item.Id));
            })
            .ToArray();
    }

    public void Release(ISet<GalleryCardViewModel>? preservedCards = null)
    {
        if (!IsRealized) return;
        foreach (var item in Items)
        {
            if (preservedCards?.Contains(item) != true) item.CancelThumbnailLoad();
        }
        Items = [];
    }

    public bool HasSameItems(GalleryRow other)
    {
        if (_layoutItems.Count != other._layoutItems.Count) return false;
        for (var index = 0; index < _layoutItems.Count; index++)
        {
            if (_layoutItems[index].Item.Id != other._layoutItems[index].Item.Id) return false;
        }
        return true;
    }

    public bool CanReuseFrom(GalleryRow other)
    {
        if (!HasSameItems(other)) return false;
        for (var index = 0; index < Items.Count; index++)
        {
            if (!Items[index].CanReuseFor(other._layoutItems[index].Item)) return false;
        }
        return true;
    }

    public void UpdateFrom(GalleryRow other, Func<long, bool> isSelected)
    {
        if (!CanReuseFrom(other))
        {
            throw new InvalidOperationException("Only rows with reusable stable item IDs can preserve realized card state.");
        }

        _layoutItems = other._layoutItems;
        IsFilled = other.IsFilled;
        _bottomSpacing = other._bottomSpacing;
        _panelX = other._panelX;
        _panelY = other._panelY;
        _panelWidth = other._panelWidth;
        SourceIndex = other.SourceIndex;
        ColumnIndex = other.ColumnIndex;
        LayoutVersion = other.LayoutVersion;
        OnPropertyChanged(nameof(RowHeight));
        OnPropertyChanged(nameof(RowMargin));
        OnPropertyChanged(nameof(PanelX));
        OnPropertyChanged(nameof(PanelY));
        OnPropertyChanged(nameof(PanelWidth));
        OnPropertyChanged(nameof(PanelBottom));
        OnPropertyChanged(nameof(SourceIndex));
        OnPropertyChanged(nameof(ColumnIndex));
        OnPropertyChanged(nameof(LayoutVersion));
        for (var index = 0; index < Items.Count; index++)
        {
            Items[index].UpdateFrom(
                _layoutItems[index].Item,
                _layoutItems[index].LayoutX,
                _layoutItems[index].LayoutY,
                _layoutItems[index].LayoutWidth,
                _layoutItems[index].ImageHeight,
                isSelected(_layoutItems[index].Item.Id));
        }
    }

    public void OffsetPanelY(double delta)
    {
        if (Math.Abs(delta) < 0.001) return;
        _panelY += delta;
        OnPropertyChanged(nameof(PanelY));
        OnPropertyChanged(nameof(PanelBottom));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static double RowContentWidth(IReadOnlyList<GalleryCardLayout> items) =>
        items.Count == 0
            ? 0
            : items.Max(item => item.LayoutX + item.LayoutWidth);
}

public enum GalleryLayoutMode
{
    Waterfall,
    Justified
}

public static class ImageAspectRatioFormatter
{
    public static string Format(int width, int height)
    {
        if (width <= 0 || height <= 0) return "宽高比未知";
        var divisor = GreatestCommonDivisor(width, height);
        var ratio = width / (double)height;
        var orientation = ratio > 1.03 ? "横图" : ratio < 0.97 ? "竖图" : "方图";
        return $"{width / divisor}:{height / divisor}  ·  {ratio:0.###}  ·  {orientation}";
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return Math.Max(1, left);
    }
}

public sealed record GalleryLayoutOptions(
    GalleryLayoutMode Mode,
    double HorizontalSpacing,
    double VerticalSpacing,
    double TargetSize)
{
    public GalleryLayoutOptions(
        GalleryLayoutMode mode,
        double spacing,
        double targetSize)
        : this(mode, spacing, spacing, targetSize)
    {
    }

    public double Spacing => HorizontalSpacing;

    public static GalleryLayoutOptions Default { get; } = new(
        GalleryLayoutMode.Waterfall,
        GalleryLayoutEngine.DefaultSpacing,
        GalleryLayoutEngine.DefaultSpacing,
        GalleryLayoutEngine.DefaultTargetSize);

    public GalleryLayoutOptions Normalize() => new(
        Mode,
        Math.Clamp(HorizontalSpacing, 0, GalleryLayoutEngine.MaximumSpacing),
        Math.Clamp(VerticalSpacing, 0, GalleryLayoutEngine.MaximumSpacing),
        Math.Clamp(TargetSize, GalleryLayoutEngine.MinimumTargetSize, GalleryLayoutEngine.MaximumTargetSize));
}

public static class GalleryLayoutEngine
{
    public const double DefaultSpacing = 14;
    public const double MinimumSpacing = 6;
    public const double MaximumSpacing = 30;
    public const double DefaultTargetSize = 320;
    public const double MinimumTargetSize = 180;
    public const double MaximumTargetSize = 520;
    private const int CurrentLayoutVersion = 7;

    public static IReadOnlyList<GalleryRow> CreateRows(
        IReadOnlyList<GalleryEntry> items,
        double availableWidth,
        GalleryLayoutOptions? options = null)
    {
        options = (options ?? GalleryLayoutOptions.Default).Normalize();
        return options.Mode == GalleryLayoutMode.Waterfall
            ? CreateWaterfallRows(items, availableWidth, options)
            : CreateJustifiedRows(items, availableWidth, options);
    }

    private static IReadOnlyList<GalleryRow> CreateWaterfallRows(
        IReadOnlyList<GalleryEntry> items,
        double availableWidth,
        GalleryLayoutOptions options)
    {
        var state = CreateInitialWaterfallState(availableWidth, options);
        return AppendWaterfallRows(items, state, options).Rows;
    }

    private static IReadOnlyList<GalleryRow> CreateJustifiedRows(
        IReadOnlyList<GalleryEntry> items,
        double availableWidth,
        GalleryLayoutOptions options)
    {
        var rows = new List<GalleryRow>();
        if (items.Count == 0) return rows;

        var targetImageHeight = options.TargetSize * 0.7;
        var pending = new List<GalleryEntry>();
        var ratioSum = 0d;
        var panelY = 0d;
        foreach (var item in items)
        {
            pending.Add(item);
            ratioSum += LayoutRatio(item);
            var projectedWidth = ratioSum * targetImageHeight
                + Math.Max(0, pending.Count - 1) * options.HorizontalSpacing;
            if (projectedWidth < availableWidth && pending.Count < 12) continue;
            var row = CreateJustifiedRow(
                pending,
                ratioSum,
                availableWidth,
                targetImageHeight,
                options.HorizontalSpacing,
                true,
                panelY);
            rows.Add(row);
            panelY += row.RowHeight + options.VerticalSpacing;
            pending.Clear();
            ratioSum = 0;
        }

        if (pending.Count > 0)
        {
            rows.Add(CreateJustifiedRow(
                pending,
                ratioSum,
                availableWidth,
                targetImageHeight,
                options.HorizontalSpacing,
                false,
                panelY));
        }

        return rows;
    }

    public static GalleryLayoutAppend CreateAppend(
        IReadOnlyList<GalleryRow> existingRows,
        IReadOnlyList<GalleryEntry> appendedItems,
        double availableWidth,
        GalleryLayoutOptions? options = null)
    {
        if (appendedItems.Count == 0)
        {
            return new GalleryLayoutAppend(false, []);
        }

        options = (options ?? GalleryLayoutOptions.Default).Normalize();
        if (options.Mode == GalleryLayoutMode.Waterfall)
        {
            var state = RestoreWaterfallState(existingRows, availableWidth, options);
            if (state.LaidOutItemCount != existingRows.Count)
            {
                var allItems = existingRows
                    .SelectMany(row => row.LayoutItems)
                    .Select(item => item.Item)
                    .Concat(appendedItems)
                    .ToArray();
                return new GalleryLayoutAppend(
                    existingRows.Count > 0,
                    CreateWaterfallRows(allItems, availableWidth, options));
            }

            return AppendWaterfallRows(appendedItems, state, options);
        }

        var replaceIncompleteTail = existingRows.LastOrDefault() is { IsFilled: false };
        var tail = new List<GalleryEntry>();
        if (replaceIncompleteTail)
        {
            tail.AddRange(existingRows[^1].LayoutItems.Select(item => item.Item));
        }
        tail.AddRange(appendedItems);
        var createdRows = CreateRows(tail, availableWidth, options);
        var startY = replaceIncompleteTail
            ? existingRows[^1].PanelY
            : existingRows.LastOrDefault()?.PanelBottom + options.VerticalSpacing ?? 0;
        foreach (var row in createdRows)
        {
            row.OffsetPanelY(startY);
        }
        return new GalleryLayoutAppend(
            replaceIncompleteTail,
            createdRows);
    }

    private static GalleryRow CreateJustifiedRow(
        IReadOnlyList<GalleryEntry> items,
        double ratioSum,
        double availableWidth,
        double targetImageHeight,
        double spacing,
        bool fill,
        double panelY = 0)
    {
        var imageHeight = fill
            ? Math.Clamp(
                (availableWidth - Math.Max(0, items.Count - 1) * spacing) / Math.Max(ratioSum, 0.01),
                targetImageHeight * 0.64,
                targetImageHeight * 1.3)
            : targetImageHeight;
        var layouts = new GalleryCardLayout[items.Count];
        var totalWidth = items.Sum(item => Math.Max(92, LayoutRatio(item) * imageHeight))
            + Math.Max(0, items.Count - 1) * spacing;
        var x = Math.Max(0, (availableWidth - totalWidth) / 2);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var width = Math.Max(92, LayoutRatio(item) * imageHeight);
            layouts[index] = new GalleryCardLayout(item, x, 0, width, imageHeight);
            x += width + spacing;
        }
        return new GalleryRow(
            layouts,
            fill,
            0,
            0,
            panelY,
            availableWidth,
            0,
            -1,
            CurrentLayoutVersion);
    }

    private static int ShortestColumn(IReadOnlyList<double> heights)
    {
        var shortest = 0;
        for (var index = 1; index < heights.Count; index++)
        {
            if (heights[index] < heights[shortest]) shortest = index;
        }
        return shortest;
    }

    public static double LayoutRatio(GalleryEntry item)
    {
        if (item.Width <= 0 || item.Height <= 0) return 1d;
        var ratio = item.Width / (double)item.Height;
        return double.IsFinite(ratio) && ratio > 0 ? ratio : 1d;
    }

    private static GalleryLayoutState CreateInitialWaterfallState(
        double availableWidth,
        GalleryLayoutOptions options)
    {
        var safeWidth = double.IsFinite(availableWidth) && availableWidth > 0
            ? availableWidth
            : options.TargetSize;
        var columnCount = Math.Clamp(
            (int)Math.Floor((safeWidth + options.HorizontalSpacing) / (options.TargetSize + options.HorizontalSpacing)),
            1,
            12);
        var cardWidth = Math.Max(
            1,
            (safeWidth - (columnCount - 1) * options.HorizontalSpacing) / columnCount);
        return new GalleryLayoutState(
            safeWidth,
            cardWidth,
            columnCount,
            options.HorizontalSpacing,
            options.VerticalSpacing,
            new double[columnCount],
            0,
            CurrentLayoutVersion);
    }

    private static GalleryLayoutState RestoreWaterfallState(
        IReadOnlyList<GalleryRow> existingRows,
        double availableWidth,
        GalleryLayoutOptions options)
    {
        var initial = CreateInitialWaterfallState(availableWidth, options);
        if (existingRows.Count == 0) return initial;
        if (existingRows.Any(row =>
                row.LayoutVersion != CurrentLayoutVersion
                || row.ColumnIndex < 0
                || row.ColumnIndex >= initial.ColumnCount
                || Math.Abs(row.PanelWidth - initial.TargetSize) > 0.01))
        {
            return initial;
        }

        var heights = new double[initial.ColumnCount];
        foreach (var row in existingRows)
        {
            heights[row.ColumnIndex] = Math.Max(
                heights[row.ColumnIndex],
                row.PanelBottom + initial.VerticalSpacing);
        }

        return initial with
        {
            ColumnHeights = heights,
            LaidOutItemCount = existingRows.Count
        };
    }

    private static GalleryLayoutAppend AppendWaterfallRows(
        IReadOnlyList<GalleryEntry> items,
        GalleryLayoutState state,
        GalleryLayoutOptions options)
    {
        if (items.Count == 0) return new GalleryLayoutAppend(false, []);
        var heights = state.ColumnHeights.ToArray();
        var rows = new GalleryRow[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var column = ShortestColumn(heights);
            var x = column == state.ColumnCount - 1
                ? state.AvailableWidth - state.TargetSize
                : column * (state.TargetSize + state.HorizontalSpacing);
            var y = heights[column];
            if (item.Width <= 0 || item.Height <= 0)
            {
                DevelopmentPerformanceTrace.Event("gallery-invalid-layout-metadata", new
                {
                    itemId = item.Id,
                    item.Width,
                    item.Height,
                    fallbackRatio = 1
                });
            }
            var imageHeight = state.TargetSize / LayoutRatio(item);
            if (!double.IsFinite(imageHeight) || imageHeight <= 0)
            {
                imageHeight = state.TargetSize;
                DevelopmentPerformanceTrace.Event("gallery-invalid-layout-metadata", new
                {
                    itemId = item.Id,
                    item.Width,
                    item.Height
                });
            }

            var sourceIndex = state.LaidOutItemCount + index;
            var geometry = new GalleryItemGeometry(
                item,
                sourceIndex,
                column,
                x,
                y,
                state.TargetSize,
                imageHeight,
                state.LayoutVersion);
            rows[index] = new GalleryRow(
                [new GalleryCardLayout(item, 0, 0, geometry.Width, geometry.Height)],
                true,
                0,
                geometry.X,
                geometry.Y,
                geometry.Width,
                geometry.SourceIndex,
                geometry.ColumnIndex,
                geometry.LayoutVersion);
            heights[column] = y + imageHeight + options.VerticalSpacing;
        }

        return new GalleryLayoutAppend(false, rows);
    }
}

public static class GalleryVirtualizationPolicy
{
    public const int PageSize = 240;
    public const double PrefetchViewports = 2d;

    public static bool ShouldPrefetch(double viewportHeight, double scrollableHeight, double verticalOffset)
    {
        if (viewportHeight <= 0) return false;
        if (scrollableHeight <= 0) return true;
        var remaining = scrollableHeight - verticalOffset;
        return remaining <= Math.Max(600, viewportHeight * PrefetchViewports);
    }
}
