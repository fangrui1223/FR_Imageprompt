using System.Diagnostics;

namespace PromptVault.App.Services;

internal sealed class ClipboardFileEventCoalescer
{
    private readonly TimeSpan _window;
    private readonly long _timestampFrequency;
    private string? _lastPath;
    private long _lastTimestamp;

    public ClipboardFileEventCoalescer(
        TimeSpan? window = null,
        long? timestampFrequency = null)
    {
        _window = window ?? TimeSpan.FromSeconds(2);
        _timestampFrequency = timestampFrequency ?? Stopwatch.Frequency;
    }

    public bool ShouldAccept(string path, long observedTimestamp)
    {
        var isDuplicate = string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase)
            && observedTimestamp >= _lastTimestamp
            && TimeSpan.FromSeconds(
                (observedTimestamp - _lastTimestamp) / (double)_timestampFrequency) <= _window;
        if (isDuplicate) return false;
        _lastPath = path;
        _lastTimestamp = observedTimestamp;
        return true;
    }
}
