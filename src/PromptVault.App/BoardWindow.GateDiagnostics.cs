using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public partial class BoardWindow
{
    internal async Task RunM8GateSmokeAsync(string reportPath, AppSettings settings)
    {
        EnsureIsolatedM8Settings(settings, "M8-08 5,000 项总门禁");
        if (_items.Count != 5_000)
            throw new InvalidOperationException($"M8-08 总门禁需要 5,000 项隔离画板，实际为 {_items.Count:N0} 项。");
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
        await WaitForAnyBoardImageAsync();

        const int warmupFrames = 30;
        const int targetFrames = 181;
        var initialViewport = _viewport;
        var originalItems = SnapshotItems();
        var selected = BoardViewportEngine.QueryVisible(_items, _viewport).Take(2).Select(item => item.Id).ToHashSet();
        _selectedIds.Clear();
        _selectedIds.UnionWith(selected);
        var selectionBounds = BoardCameraEngine.SelectionBounds(_items, _selectedIds, [], null)!.Bounds;
        var frameTimes = new List<double>(targetFrames);
        var callbackTimes = new List<double>(targetFrames);
        var maximumRealized = 0;
        var nearBlackFrames = 0;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan lastRendering = TimeSpan.Zero;
        long lastCallback = 0;
        var frameIndex = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? rendering = null;
        rendering = (_, args) =>
        {
            if (args is not RenderingEventArgs frame || frame.RenderingTime == lastRendering) return;
            var callback = Stopwatch.GetTimestamp();
            if (frameIndex >= warmupFrames && lastRendering != TimeSpan.Zero)
            {
                frameTimes.Add((frame.RenderingTime - lastRendering).TotalMilliseconds);
                callbackTimes.Add((callback - lastCallback) * 1000d / Stopwatch.Frequency);
            }
            lastRendering = frame.RenderingTime;
            lastCallback = callback;
            maximumRealized = Math.Max(maximumRealized, _realized.Count);
            if (_realized.Count > 0 && !_realized.Values.Any(element => FindItemImage(element)?.Source is not null))
                nearBlackFrames++;

            var measuredIndex = Math.Max(0, frameIndex - warmupFrames);
            var phase = measuredIndex / (double)targetFrames;
            _viewport = initialViewport with
            {
                OffsetX = initialViewport.OffsetX - measuredIndex * 12,
                OffsetY = initialViewport.OffsetY - measuredIndex * 3,
                Zoom = BoardViewportEngine.ClampZoom(initialViewport.Zoom * (1 + 0.08 * Math.Sin(phase * Math.PI * 4)))
            };
            if (measuredIndex >= 120)
            {
                var target = BoardTransformEngine.ResizeBounds(
                    selectionBounds,
                    BoardResizeHandle.BottomRight,
                    20 * Math.Sin(phase * Math.PI * 6),
                    14 * Math.Sin(phase * Math.PI * 6),
                    true,
                    false);
                ReplaceItems(BoardTransformEngine.ScaleSelection(originalItems, selected, selectionBounds, target));
            }
            ApplyViewportMatrix();
            RenderVisibleItems();
            frameIndex++;
            if (frameIndex < warmupFrames + targetFrames) return;
            CompositionTarget.Rendering -= rendering;
            completed.TrySetResult();
        };
        CompositionTarget.Rendering += rendering;
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        stopwatch.Stop();
        ReplaceItems(originalItems);
        _viewport = initialViewport;
        ApplyViewportMatrix();
        RenderVisibleItems();

        frameTimes.Sort();
        callbackTimes.Sort();
        var p50 = Percentile(frameTimes, 0.50);
        var p95 = Percentile(frameTimes, 0.95);
        var p99 = Percentile(frameTimes, 0.99);
        var maximum = frameTimes.Count == 0 ? 0 : frameTimes[^1];
        for (var index = 0; index < 50; index++)
            _ = BoardViewportEngine.QueryVisible(_items, initialViewport with { OffsetX = 90 - index * 18 });
        var querySamples = new double[301];
        var maxCandidates = 0;
        for (var index = 0; index < querySamples.Length; index++)
        {
            var moving = initialViewport with { OffsetX = 90 - index * 18, OffsetY = 70 - index * 6 };
            var query = Stopwatch.StartNew();
            maxCandidates = Math.Max(maxCandidates, BoardViewportEngine.QueryVisible(_items, moving).Count);
            query.Stop();
            querySamples[index] = query.Elapsed.TotalMilliseconds;
        }
        Array.Sort(querySamples);
        var queryP95 = Percentile(querySamples, 0.95);
        var dpi = VisualTreeHelper.GetDpi(this);
        var process = Process.GetCurrentProcess();
        var passed = frameTimes.Count >= 175 && p95 <= 16.949 && p99 <= 33.898 && maximum <= 33.898
            && nearBlackFrames == 0 && maximumRealized < 300 && queryP95 <= 0.10;
        var report = new
        {
            Milestone = "M8-08-5000-item-real-wpf-frame-gate",
            GeneratedAt = DateTimeOffset.Now,
            DataKind = "synthetic",
            Display = new
            {
                PhysicalWidth = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                PhysicalHeight = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY,
                AppliedDpi = dpi.PixelsPerInchX,
                DpiScale = dpi.DpiScaleX,
                RefreshRateHz = 59,
                OneRefreshPeriodMs = 16.949,
                TwoRefreshPeriodsMs = 33.898
            },
            Board = new
            {
                ItemCount = _items.Count,
                Samples = frameTimes.Count,
                DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                FrameP50Ms = p50,
                FrameP95Ms = p95,
                FrameP99Ms = p99,
                FrameMaximumMs = Math.Round(maximum, 3),
                CallbackP95Ms = Percentile(callbackTimes, 0.95),
                MaximumRealizedElements = maximumRealized,
                NearBlackFrames = nearBlackFrames,
                ViewportQueryP95Ms = queryP95,
                MaximumVisibleCandidates = maxCandidates,
                ContinuousPan = true,
                ContinuousZoom = true,
                ContinuousMultiTransform = true
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
        SetStatus(passed ? "M8-08 5,000 项总门禁通过" : "M8-08 5,000 项总门禁失败");
    }

    private async Task WaitForAnyBoardImageAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!_realized.Values.Any(element => FindItemImage(element)?.Source is not null)
               && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1);
        return Math.Round(ordered[index], 3);
    }
}
