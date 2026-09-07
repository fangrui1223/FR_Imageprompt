using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class MainWindow
{
    private CancellationTokenSource? _rollingWarmCancellation;
    private DispatcherTimer? _rollingWarmTimer;
    private Task? _fastBrowseBackgroundTask;
    private bool _isFastBrowseIndexing;

    private async Task<GalleryEntry?> EnsureFullEntryAsync(
        GalleryEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.IsExternal || entry.HasFullMetadata) return entry;
        var item = await _repository.GetGalleryItemAsync(entry.Id, cancellationToken);
        return item is null ? null : GalleryEntry.FromLibrary(item);
    }

    private async Task<GalleryEntry[]> EnsureFullEntriesAsync(
        IEnumerable<GalleryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var hydrated = new List<GalleryEntry>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await EnsureFullEntryAsync(entry, cancellationToken) is { } full)
            {
                hydrated.Add(full);
            }
        }
        return hydrated.ToArray();
    }

    private static GalleryEntry MergeBrowseStateWithFullMetadata(
        GalleryEntry browse,
        GalleryEntry full) =>
        browse with
        {
            Prompt = full.Prompt,
            Notes = full.Notes,
            HasFullMetadata = true
        };

    private void StartFastBrowseBackground(
        SearchOptions search,
        FastBrowseGenerationLease lease)
    {
        _isFastBrowseIndexing = true;
        _fastBrowseBackgroundTask = LoadFastBrowseIndexAndWarmAsync(search, lease);
        _ = ObserveFastBrowseIndexAsync(
            _fastBrowseBackgroundTask,
            lease.Generation);
    }

    private async Task ObserveFastBrowseIndexAsync(Task task, int generation)
    {
        try
        {
            await ObserveFastBrowseBackgroundAsync(task);
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_fastBrowseGenerations.IsCurrent(generation))
                    {
                        _isFastBrowseIndexing = false;
                    }
                });
            }
        }
    }

    private static async Task ObserveFastBrowseBackgroundAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "fast-browse-background",
                "Adaptive fast browsing background work failed.",
                ex);
        }
    }

    private async Task LoadFastBrowseIndexAndWarmAsync(
        SearchOptions search,
        FastBrowseGenerationLease lease)
    {
        var clock = Stopwatch.StartNew();
        var entries = search.Source == GallerySourceKind.ExternalFolder
            ? await LoadAllExternalBrowseEntriesAsync(search, lease.CancellationToken)
                .ConfigureAwait(false)
            : await LoadAllManagedBrowseEntriesAsync(search, lease.CancellationToken)
                .ConfigureAwait(false);
        lease.CancellationToken.ThrowIfCancellationRequested();
        if (!_fastBrowseGenerations.IsCurrent(lease.Generation)) return;

        var apply = await Dispatcher.InvokeAsync(
            () => ApplyFastBrowseIndex(entries, lease.Generation),
            DispatcherPriority.Background);
        if (!apply.Applied) return;

        var warmRequestCount = apply.Plan.WarmItemLimit;
        var backgroundWarmAwaited = apply.Plan.Tier != AdaptiveGalleryTier.LargeRollingWindow;
        if (!backgroundWarmAwaited)
        {
            await Dispatcher.InvokeAsync(
                QueueRollingWarmup,
                DispatcherPriority.Background);
        }
        else
        {
            var order = FastBrowsePrefetchPlanner.CreateBackgroundWarmOrder(
                apply.Entries.Length,
                apply.CenterIndex,
                apply.Plan,
                apply.Direction);
            warmRequestCount = order.Count;
            await WarmMotionEntriesAsync(
                apply.Entries,
                order,
                apply.Plan,
                lease.CancellationToken).ConfigureAwait(false);
        }

        clock.Stop();
        var motion = MotionThumbnailCache.GetSnapshot();
        DevelopmentPerformanceTrace.Event("fast-browse-background-complete", new
        {
            totalItems = apply.Entries.Length,
            tier = apply.Plan.Tier.ToString(),
            motionPixels = apply.Plan.MotionPixels,
            warmItemLimit = apply.Plan.WarmItemLimit,
            warmRequestCount,
            backgroundWarmAwaited,
            elapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3),
            motion.CacheEntryCount,
            motion.CachedBytes,
            motion.MemoryBudgetBytes,
            motion.MaximumObservedConcurrency
        });
    }

    private async Task<GalleryEntry[]> LoadAllManagedBrowseEntriesAsync(
        SearchOptions search,
        CancellationToken cancellationToken)
    {
        var entries = new List<GalleryEntry>();
        GalleryPageCursor? cursor = null;
        do
        {
            var page = await _repository.SearchBrowsePageAsync(
                search with
                {
                    PageSize = 1_000,
                    Cursor = cursor
                },
                cancellationToken).ConfigureAwait(false);
            entries.AddRange(page.Items.Select(GalleryEntry.FromBrowse));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return entries.ToArray();
    }

    private async Task<GalleryEntry[]> LoadAllExternalBrowseEntriesAsync(
        SearchOptions search,
        CancellationToken cancellationToken)
    {
        var folder = _settings.ExternalFolders.FirstOrDefault(
            item => item.Id == search.SourceId);
        if (folder is null) return [];
        var entries = new List<GalleryEntry>();
        ExternalFilePageCursor? cursor = null;
        do
        {
            var page = await _repository.SearchExternalFilesAsync(
                folder.Id,
                search.Query,
                search.Sort,
                1_000,
                cursor,
                cancellationToken);
            entries.AddRange(page.Items.Select(
                item => GalleryEntry.FromExternal(item, folder.Path)));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return entries.ToArray();
    }

    private FastBrowseApplyResult ApplyFastBrowseIndex(
        GalleryEntry[] backgroundEntries,
        int generation)
    {
        if (_galleryRowsTransferredOut || _windowLifetime.IsCancellationRequested
            || !_fastBrowseGenerations.IsCurrent(generation)
            || backgroundEntries.Length == 0 && _totalCount > 0)
        {
            return FastBrowseApplyResult.NotApplied;
        }

        var anchor = CaptureLayoutVisualAnchor();
        var fullById = _items
            .Where(item => item.HasFullMetadata)
            .ToDictionary(item => item.Id);
        var entries = backgroundEntries
            .Select(item => fullById.TryGetValue(item.Id, out var full)
                ? MergeBrowseStateWithFullMetadata(item, full)
                : item)
            .DistinctBy(item => item.Id)
            .ToArray();
        var width = GetGalleryAvailableWidth();
        var rows = GalleryLayoutEngine.CreateRows(
            entries,
            width,
            CurrentGalleryLayoutOptions());
        var reusableCards = CreateReusableCardMap(entries);
        var reusableIds = reusableCards.Keys.ToHashSet();
        var additionalRows = rows
            .Select((row, index) => (row, index))
            .Where(candidate => candidate.row.LayoutItems.Any(
                item => reusableIds.Contains(item.Item.Id)))
            .Select(candidate => candidate.index)
            .ToHashSet();
        ApplyPreparedRows(
            entries,
            rows,
            width,
            new GalleryPreparation(
                reusableCards,
                new Dictionary<long, GalleryCardViewModel>(),
                CountRowsForFirstViewport(rows),
                additionalRows),
            resetScroll: false,
            bulkRows: true);
        _totalCount = entries.LongLength;
        _hasMoreItems = false;
        _nextPageCursor = null;
        _nextExternalPageCursor = null;
        _isLoadingNextPage = false;
        _isFastBrowseIndexing = false;
        UpdateCountText();
        ApplySelectionState();
        RestoreInspectorAfterRefresh();
        RestoreLayoutVisualAnchor(anchor);

        var centerIndex = anchor is null
            ? 0
            : Array.FindIndex(entries, item => item.Id == anchor.ItemId);
        if (centerIndex < 0) centerIndex = 0;
        DevelopmentPerformanceTrace.Event("fast-browse-index-applied", new
        {
            items = entries.Length,
            rows = rows.Count,
            lightweightItems = entries.Count(item => !item.HasFullMetadata),
            reusedCards = reusableCards.Count,
            tier = _fastBrowsePlan.Tier.ToString()
        });
        return new FastBrowseApplyResult(
            true,
            entries,
            Math.Clamp(centerIndex, 0, Math.Max(0, entries.Length - 1)),
            _galleryScrollDirection,
            _fastBrowsePlan);
    }

    private async Task WarmMotionEntriesAsync(
        IReadOnlyList<GalleryEntry> entries,
        IReadOnlyList<int> order,
        AdaptiveFastBrowsePlan plan,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < order.Count; offset += plan.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = order
                .Skip(offset)
                .Take(plan.BatchSize)
                .Select(index => WarmMotionEntryAsync(
                    entries[index],
                    plan.MotionPixels,
                    cancellationToken))
                .ToArray();
            await Task.WhenAll(batch).ConfigureAwait(false);
            var snapshot = MotionThumbnailCache.GetSnapshot();
            DevelopmentPerformanceTrace.Event("fast-browse-warm-batch", new
            {
                tier = plan.Tier.ToString(),
                completed = Math.Min(offset + batch.Length, order.Count),
                requested = order.Count,
                snapshot.CacheHitCount,
                snapshot.DecodeCount,
                snapshot.CanceledBeforeStartCount,
                snapshot.PendingCount,
                snapshot.RunningCount,
                snapshot.MaximumObservedConcurrency,
                snapshot.CachedBytes,
                snapshot.MemoryBudgetBytes
            });
            await Task.Yield();
        }
    }

    private async Task WarmMotionEntryAsync(
        GalleryEntry entry,
        int motionPixels,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = entry.IsExternal
                ? entry.ThumbnailPath
                : _repository.Paths.ToAbsolute(entry.ThumbnailPath);
            _ = await MotionThumbnailCache.LoadAsync(
                path,
                motionPixels,
                ThumbnailRequestPriority.Prefetch,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "fast-browse-warm-item",
                $"Motion preview could not be warmed for item {entry.Id}.",
                ex);
        }
    }

    private void QueueRollingWarmup()
    {
        if (_fastBrowsePlan.Tier != AdaptiveGalleryTier.LargeRollingWindow) return;
        _rollingWarmTimer ??= CreateRollingWarmTimer();
        _rollingWarmTimer.Stop();
        _rollingWarmTimer.Start();
    }

    private DispatcherTimer CreateRollingWarmTimer()
    {
        var timer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            StartRollingWarmupAtCurrentViewport();
        };
        return timer;
    }

    private void StartRollingWarmupAtCurrentViewport()
    {
        if (_fastBrowsePlan.Tier != AdaptiveGalleryTier.LargeRollingWindow
            || !_fastBrowseGenerations.IsCurrent(_fastBrowseLease.Generation)
            || _items.Count == 0)
        {
            return;
        }

        _rollingWarmCancellation?.Cancel();
        _rollingWarmCancellation?.Dispose();
        _rollingWarmCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _fastBrowseLease.CancellationToken);
        var centerIndex = FindCurrentViewportCenterIndex();
        var order = FastBrowsePrefetchPlanner.CreateOrderedWindow(
            _items.Count,
            centerIndex,
            _fastBrowsePlan.WarmItemLimit,
            _galleryScrollDirection);
        var entries = _items.ToArray();
        _ = ObserveFastBrowseBackgroundAsync(WarmMotionEntriesAsync(
            entries,
            order,
            _fastBrowsePlan,
            _rollingWarmCancellation.Token));
    }

    private int FindCurrentViewportCenterIndex()
    {
        var viewportCenter = RowsList.ActualHeight / 2;
        var bestIndex = 0;
        var bestDistance = double.MaxValue;
        foreach (var (row, element) in _realizedRowElements)
        {
            if (!element.IsLoaded) continue;
            try
            {
                var top = element.TranslatePoint(new Point(0, 0), RowsList).Y;
                var center = top + Math.Max(element.ActualHeight, row.RowHeight) / 2;
                var distance = Math.Abs(center - viewportCenter);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = row.SourceIndex;
            }
            catch (InvalidOperationException)
            {
            }
        }
        return Math.Clamp(bestIndex, 0, Math.Max(0, _items.Count - 1));
    }

    private void StopFastBrowseBackground()
    {
        _rollingWarmTimer?.Stop();
        _rollingWarmCancellation?.Cancel();
        _rollingWarmCancellation?.Dispose();
        _rollingWarmCancellation = null;
        _isFastBrowseIndexing = false;
    }

    private sealed record FastBrowseApplyResult(
        bool Applied,
        GalleryEntry[] Entries,
        int CenterIndex,
        GalleryScrollDirection Direction,
        AdaptiveFastBrowsePlan Plan)
    {
        public static readonly FastBrowseApplyResult NotApplied = new(
            false,
            [],
            0,
            GalleryScrollDirection.None,
            AdaptiveFastBrowsePolicy.Create(0, 0));
    }
}
