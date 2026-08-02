using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PromptVault.App.Services;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunImageSmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8-07 渐进高清烟测");
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

        var candidates = _items.Where(item => File.Exists(ResolveItemOriginalPath(item))).Take(3).ToArray();
        if (candidates.Length < 3) throw new InvalidOperationException("隔离画板缺少渐进加载候选项。");
        _selectedIds.Clear();
        _selectedIds.Add(candidates[0].Id);
        RenderVisibleItems();
        await WaitForPreviewAsync(candidates[0].Id);
        var preview = FindItemImage(_realized[candidates[0].Id]);
        var previewBeforeFocus = preview?.Source;
        var requestBefore = _boardHighResolutionRequestCount;
        FocusItemFromDoubleClick(candidates[0].Id);
        var previewVisibleAtFocusStart = previewBeforeFocus is not null && preview?.Source is not null;
        await WaitForMetricAsync(() => _boardHighResolutionCompleteCount > 0, TimeSpan.FromSeconds(6));
        var highResolutionCompleted = _boardHighResolutionCompleteCount > 0;
        var noBlackFrame = preview?.Source is not null;

        NavigateFocusedItem(1);
        NavigateFocusedItem(1);
        await WaitForMetricAsync(
            () => _boardHighResolutionCompleteCount > 1 || _boardHighResolutionFailureCount > 0,
            TimeSpan.FromSeconds(6));
        var currentId = _focusController.Target?.SingleItemId;
        var currentKeptImage = currentId is { } id
            && _realized.TryGetValue(id, out var currentElement)
            && FindItemImage(currentElement)?.Source is not null;

        var missing = _items.First(item => !File.Exists(ResolveItemOriginalPath(item)));
        _selectedIds.Clear();
        _selectedIds.Add(missing.Id);
        FocusItemFromDoubleClick(missing.Id);
        await WaitForCameraSettledAsync();
        await WaitForPreviewAsync(missing.Id);
        var missingKeepsThumbnail = _realized.TryGetValue(missing.Id, out var missingElement)
            && FindItemImage(missingElement)?.Source is not null;

        var cacheBefore = _boardImmersiveImageCache.GetSnapshot();
        var original = ResolveItemOriginalPath(candidates[0]);
        var width = BoardProgressiveImagePolicy.SelectDecodeWidth(1500, 900, 1.5);
        var first = await _boardImmersiveImageCache.LoadAsync(original, width);
        var second = await _boardImmersiveImageCache.LoadAsync(original, width);
        var cacheAfter = _boardImmersiveImageCache.GetSnapshot();
        var cacheReused = second.CacheHit && ReferenceEquals(first.Image, second.Image);
        var boundedCache = cacheAfter.CachedBytes <= cacheAfter.MemoryBudgetBytes;

        var dpi = VisualTreeHelper.GetDpi(this);
        var process = Process.GetCurrentProcess();
        var passed = previewVisibleAtFocusStart && highResolutionCompleted && noBlackFrame
            && currentKeptImage && missingKeepsThumbnail && cacheReused && boundedCache
            && _boardHighResolutionRequestCount > requestBefore;
        var report = new
        {
            Milestone = "M8-07-progressive-focus-image-ui-smoke",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX
            },
            Progressive = new
            {
                PreviewVisibleAtFocusStart = previewVisibleAtFocusStart,
                HighResolutionCompleted = highResolutionCompleted,
                NoBlackFrame = noBlackFrame,
                RapidNavigationKeptCurrentImage = currentKeptImage,
                MissingOriginalKeptThumbnail = missingKeepsThumbnail,
                Requests = _boardHighResolutionRequestCount,
                Completed = _boardHighResolutionCompleteCount,
                Canceled = _boardHighResolutionCancelCount,
                Failures = _boardHighResolutionFailureCount,
                CacheHits = _boardHighResolutionCacheHitCount,
                CacheReused = cacheReused,
                DecodeWidth = width
            },
            SharedCache = new
            {
                EntriesBefore = cacheBefore.EntryCount,
                EntriesAfter = cacheAfter.EntryCount,
                cacheAfter.CachedBytes,
                cacheAfter.MemoryBudgetBytes,
                Bounded = boundedCache
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
        SetStatus(passed ? "M8-07 隔离渐进高清烟测通过" : "M8-07 隔离渐进高清烟测失败");
    }

    private async Task WaitForPreviewAsync(long itemId)
    {
        await WaitForMetricAsync(
            () => _realized.TryGetValue(itemId, out var element) && FindItemImage(element)?.Source is not null,
            TimeSpan.FromSeconds(4));
    }

    private static async Task WaitForMetricAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }
}
