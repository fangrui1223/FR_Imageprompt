using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Media;

namespace PromptVault.App.Services;

internal static class DevelopmentPerformanceTrace
{
#if DEBUG || PROMPTVAULT_PERF_DIAGNOSTICS
    private static readonly Stopwatch ProcessClock = Stopwatch.StartNew();
    private static readonly bool EnabledValue =
        string.Equals(Environment.GetEnvironmentVariable("PROMPTVAULT_DIAGNOSTICS"), "1", StringComparison.Ordinal);
    private static readonly string LogPath = ResolveLogPath();
    private static readonly Lazy<TraceWriter> Writer = new(() => new TraceWriter(LogPath));
#endif

    public static bool IsEnabled
    {
        get
        {
#if DEBUG || PROMPTVAULT_PERF_DIAGNOSTICS
            return EnabledValue;
#else
            return false;
#endif
        }
    }

    public static int AutoRunScrollProbeCount
    {
        get
        {
#if DEBUG || PROMPTVAULT_PERF_DIAGNOSTICS
            if (!EnabledValue) return 0;
            return int.TryParse(
                Environment.GetEnvironmentVariable("PROMPTVAULT_AUTO_SCROLL_PROBE"),
                out var count)
                ? Math.Clamp(count, 0, 5)
                : 0;
#else
            return 0;
#endif
        }
    }

    public static IDisposable Measure(string name, object? data = null)
    {
#if DEBUG || PROMPTVAULT_PERF_DIAGNOSTICS
        return EnabledValue ? new Measurement(name, data) : EmptyMeasurement.Instance;
#else
        return EmptyMeasurement.Instance;
#endif
    }

    public static void Event(string name, object? data = null)
    {
#if DEBUG || PROMPTVAULT_PERF_DIAGNOSTICS
        if (EnabledValue) Write(name, null, data);
#endif
    }

#if DEBUG || PROMPTVAULT_PERF_DIAGNOSTICS
    private static void Write(string name, double? elapsedMilliseconds, object? data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var entry = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId,
                processElapsedMs = Math.Round(ProcessClock.Elapsed.TotalMilliseconds, 3),
                name,
                elapsedMs = elapsedMilliseconds is null ? (double?)null : Math.Round(elapsedMilliseconds.Value, 3),
                data
            };
            var json = JsonSerializer.Serialize(entry);
            Writer.Value.TryWrite(json);
        }
        catch
        {
            // Opt-in diagnostics must never change application behavior.
        }
    }

    private static string ResolveLogPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("PROMPTVAULT_DIAGNOSTICS_PATH");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptVault",
                "diagnostics",
                "performance.jsonl")
            : Path.GetFullPath(overridePath);
    }

    private sealed class Measurement(string name, object? data) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stopwatch.Stop();
            Write(name, _stopwatch.Elapsed.TotalMilliseconds, data);
        }
    }

    private sealed class TraceWriter
    {
        private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private readonly string _path;

        public TraceWriter(string path)
        {
            _path = path;
            _ = Task.Run(WriteLoopAsync);
        }

        public void TryWrite(string value) => _channel.Writer.TryWrite(value);

        private async Task WriteLoopAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                await using var stream = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    16 * 1024,
                    useAsync: true);
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                await foreach (var entry in _channel.Reader.ReadAllAsync())
                {
                    await writer.WriteLineAsync(entry);
                }
            }
            catch
            {
                // Opt-in diagnostics must never change application behavior.
            }
        }
    }
#endif

    private sealed class EmptyMeasurement : IDisposable
    {
        public static EmptyMeasurement Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class DevelopmentFrameSampler : IDisposable
{
    private readonly List<double> _frameTimes = [];
    private readonly List<double> _callbackArrivalTimes = [];
    private string _interaction = "";
    private long _activeUntil;
    private TimeSpan _lastRenderingTime;
    private long _lastCallbackTimestamp;
    private bool _attached;

    public DevelopmentFrameSampler()
    {
        if (!DevelopmentPerformanceTrace.IsEnabled) return;
        CompositionTarget.Rendering += OnRendering;
        _attached = true;
    }

    public void BeginInteraction(string name, TimeSpan duration)
    {
        if (!_attached) return;
        var now = Stopwatch.GetTimestamp();
        var requestedUntil = now + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        if (_activeUntil != 0
            && now <= _activeUntil
            && string.Equals(_interaction, name, StringComparison.Ordinal))
        {
            _activeUntil = Math.Max(_activeUntil, requestedUntil);
            return;
        }

        _interaction = name;
        _activeUntil = requestedUntil;
        _lastRenderingTime = TimeSpan.Zero;
        _lastCallbackTimestamp = 0;
        _frameTimes.Clear();
        _callbackArrivalTimes.Clear();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_activeUntil == 0) return;
        if (now <= _activeUntil)
        {
            if (e is not RenderingEventArgs rendering
                || rendering.RenderingTime == _lastRenderingTime)
            {
                return;
            }
            if (_lastRenderingTime != TimeSpan.Zero && _lastCallbackTimestamp != 0)
            {
                _frameTimes.Add((rendering.RenderingTime - _lastRenderingTime).TotalMilliseconds);
                _callbackArrivalTimes.Add(
                    (now - _lastCallbackTimestamp)
                    * 1000d
                    / Stopwatch.Frequency);
            }
            _lastRenderingTime = rendering.RenderingTime;
            _lastCallbackTimestamp = now;
            return;
        }

        var ordered = _frameTimes.Order().ToArray();
        var callbackOrdered = _callbackArrivalTimes.Order().ToArray();
        var thumbnails = ThumbnailCache.GetSnapshot();
        DevelopmentPerformanceTrace.Event("ui-frame-sample", new
        {
            interaction = _interaction,
            frameCount = ordered.Length,
            p50Ms = Percentile(ordered, 0.50),
            p95Ms = Percentile(ordered, 0.95),
            p99Ms = Percentile(ordered, 0.99),
            maximumMs = ordered.Length == 0 ? 0 : Math.Round(ordered[^1], 3),
            callbackArrival = new
            {
                frameCount = callbackOrdered.Length,
                p50Ms = Percentile(callbackOrdered, 0.50),
                p95Ms = Percentile(callbackOrdered, 0.95),
                p99Ms = Percentile(callbackOrdered, 0.99),
                maximumMs = callbackOrdered.Length == 0
                    ? 0
                    : Math.Round(callbackOrdered[^1], 3),
                diagnosticOnly = true
            },
            thumbnails = new
            {
                requests = thumbnails.RequestCount,
                cacheHits = thumbnails.CacheHitCount,
                decodes = thumbnails.DecodeCount,
                coalesced = thumbnails.CoalescedRequestCount,
                canceledBeforeStart = thumbnails.CanceledBeforeStartCount,
                pending = thumbnails.PendingCount,
                running = thumbnails.RunningCount,
                runningPrefetch = thumbnails.RunningPrefetchCount,
                maximumConcurrency = thumbnails.MaximumObservedConcurrency,
                cacheEntries = thumbnails.CacheEntryCount,
                cachedMiB = Math.Round(thumbnails.CachedBytes / 1024d / 1024d, 3),
                budgetMiB = Math.Round(thumbnails.MemoryBudgetBytes / 1024d / 1024d, 3)
            }
        });
        _activeUntil = 0;
        _lastRenderingTime = TimeSpan.Zero;
        _lastCallbackTimestamp = 0;
        _frameTimes.Clear();
        _callbackArrivalTimes.Clear();
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        if (ordered.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return Math.Round(ordered[index], 3);
    }

    public void Dispose()
    {
        if (!_attached) return;
        CompositionTarget.Rendering -= OnRendering;
        _attached = false;
    }
}
