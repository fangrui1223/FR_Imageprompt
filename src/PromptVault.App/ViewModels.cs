using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    string? ExternalFolderId)
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
        null);

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
        item.FolderId);

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
        DeletedAt);
}

public sealed class GalleryCardViewModel : INotifyPropertyChanged
{
    private sealed record ThumbnailRequestSpec(string Path, int TargetPhysicalPixels);

    private BitmapSource? _thumbnail;
    private bool _thumbnailLoadFailed;
    private bool _isSelected;
    private double _layoutWidth;
    private double _imageHeight;
    private ThumbnailRequestPriority _thumbnailPriority = ThumbnailRequestPriority.Prefetch;
    private CancellationTokenSource? _thumbnailLoadCancellation;
    private Task? _thumbnailLoadTask;
    private ThumbnailRequestSpec? _activeThumbnailRequest;
    private ThumbnailRequestSpec? _loadedThumbnailRequest;
    private int _thumbnailLoadGeneration;

    public GalleryCardViewModel(GalleryEntry item, LibraryPaths paths, double layoutWidth, double imageHeight, bool isSelected = false)
    {
        Item = item;
        Paths = paths;
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
    public double LayoutWidth { get => _layoutWidth; private set { if (Math.Abs(_layoutWidth - value) < 0.1) return; _layoutWidth = value; OnPropertyChanged(); } }
    public double ImageHeight { get => _imageHeight; private set { if (Math.Abs(_imageHeight - value) < 0.1) return; _imageHeight = value; OnPropertyChanged(); OnPropertyChanged(nameof(CardHeight)); } }
    public double CardHeight => ImageHeight + 42;
    public BitmapSource? Thumbnail { get => _thumbnail; private set { _thumbnail = value; OnPropertyChanged(); } }
    public bool ThumbnailLoadFailed { get => _thumbnailLoadFailed; private set { if (_thumbnailLoadFailed == value) return; _thumbnailLoadFailed = value; OnPropertyChanged(); } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
    public string OriginalPath => Item.IsExternal ? Item.OriginalPath : Paths.ToAbsolute(Item.OriginalPath);
    public string ThumbnailPath => Item.IsExternal ? Item.ThumbnailPath : Paths.ToAbsolute(Item.ThumbnailPath);
    public string MediumThumbnailPath => Item.IsExternal ? Item.MediumThumbnailPath : Paths.ToAbsolute(Item.MediumThumbnailPath);

    public void UpdateLayout(double layoutWidth, double imageHeight)
    {
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
        UpdateLayout(layoutWidth, imageHeight);
        IsSelected = isSelected;

        if (itemChanged)
        {
            OnPropertyChanged(nameof(Item));
            OnPropertyChanged(nameof(IsExternal));
            OnPropertyChanged(nameof(CategoryName));
            OnPropertyChanged(nameof(Tags));
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
                }, cancellation.Token).ConfigureAwait(false);
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
                }).ConfigureAwait(false);
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
    double LayoutWidth,
    double ImageHeight);

public sealed record GalleryLayoutAppend(
    bool ReplaceIncompleteTail,
    IReadOnlyList<GalleryRow> Rows);

public sealed class GalleryRow : INotifyPropertyChanged
{
    private IReadOnlyList<GalleryCardLayout> _layoutItems;
    private IReadOnlyList<GalleryCardViewModel> _items = [];

    public GalleryRow(IReadOnlyList<GalleryCardLayout> layoutItems, bool isFilled)
    {
        _layoutItems = layoutItems;
        IsFilled = isFilled;
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
    public double RowHeight => _layoutItems.Count == 0
        ? 0
        : _layoutItems.Max(item => item.ImageHeight + 42);

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
                        layout.LayoutWidth,
                        layout.ImageHeight,
                        isSelected(layout.Item.Id));
                    transferredCards?.Add(reusable);
                    return reusable;
                }

                return new GalleryCardViewModel(
                    layout.Item,
                    paths,
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
        OnPropertyChanged(nameof(RowHeight));
        for (var index = 0; index < Items.Count; index++)
        {
            Items[index].UpdateFrom(
                _layoutItems[index].Item,
                _layoutItems[index].LayoutWidth,
                _layoutItems[index].ImageHeight,
                isSelected(_layoutItems[index].Item.Id));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class GalleryLayoutEngine
{
    public const double TargetImageHeight = 210;
    public const double HorizontalMargin = 14;

    public static IReadOnlyList<GalleryRow> CreateRows(
        IReadOnlyList<GalleryEntry> items,
        double availableWidth)
    {
        var rows = new List<GalleryRow>();
        if (items.Count == 0) return rows;

        var pending = new List<GalleryEntry>();
        var ratioSum = 0d;
        foreach (var item in items)
        {
            pending.Add(item);
            ratioSum += LayoutRatio(item);
            var projectedWidth = ratioSum * TargetImageHeight + pending.Count * HorizontalMargin;
            if (projectedWidth < availableWidth && pending.Count < 7) continue;
            rows.Add(CreateRow(pending, ratioSum, availableWidth, true));
            pending.Clear();
            ratioSum = 0;
        }

        if (pending.Count > 0)
        {
            rows.Add(CreateRow(pending, ratioSum, availableWidth, false));
        }

        return rows;
    }

    public static GalleryLayoutAppend CreateAppend(
        IReadOnlyList<GalleryRow> existingRows,
        IReadOnlyList<GalleryEntry> appendedItems,
        double availableWidth)
    {
        if (appendedItems.Count == 0)
        {
            return new GalleryLayoutAppend(false, []);
        }

        var replaceIncompleteTail = existingRows.LastOrDefault() is { IsFilled: false };
        var tail = new List<GalleryEntry>();
        if (replaceIncompleteTail)
        {
            tail.AddRange(existingRows[^1].LayoutItems.Select(item => item.Item));
        }
        tail.AddRange(appendedItems);
        return new GalleryLayoutAppend(
            replaceIncompleteTail,
            CreateRows(tail, availableWidth));
    }

    private static GalleryRow CreateRow(
        IReadOnlyList<GalleryEntry> items,
        double ratioSum,
        double availableWidth,
        bool fill)
    {
        var imageHeight = fill
            ? Math.Clamp(
                (availableWidth - items.Count * HorizontalMargin) / Math.Max(ratioSum, 0.01),
                140,
                270)
            : TargetImageHeight;
        var layouts = items
            .Select(item => new GalleryCardLayout(
                item,
                Math.Max(92, LayoutRatio(item) * imageHeight),
                imageHeight))
            .ToArray();
        return new GalleryRow(layouts, fill);
    }

    public static double LayoutRatio(GalleryEntry item)
    {
        var ratio = item.Height <= 0 ? 1d : item.Width / (double)item.Height;
        return Math.Clamp(ratio, 0.52, 2.5);
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
