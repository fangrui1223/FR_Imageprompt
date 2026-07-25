namespace PromptVault.Core;

public sealed class CapturePromptDebouncer : IDisposable
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(600);

    private readonly object _sync = new();
    private readonly Func<Guid, string, CancellationToken, Task> _onStable;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<Guid, Request> _requests = [];
    private int _disposed;

    public CapturePromptDebouncer(
        Func<Guid, string, CancellationToken, Task> onStable,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _onStable = onStable;
        _delay = delay ?? Task.Delay;
    }

    public void Submit(Guid captureId, string prompt, TimeSpan? delay = null)
    {
        var cancellation = new CancellationTokenSource();
        var request = new Request(prompt, cancellation);
        Request? previous;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                cancellation.Dispose();
                throw new ObjectDisposedException(nameof(CapturePromptDebouncer));
            }
            previous = _requests.GetValueOrDefault(captureId);
            _requests[captureId] = request;
        }
        previous?.Cancellation.Cancel();
        _ = RunAsync(captureId, request, delay ?? DefaultDelay);
    }

    public void Cancel(Guid captureId)
    {
        Request? request;
        lock (_sync)
        {
            request = _requests.Remove(captureId, out var current)
                ? current
                : null;
        }
        request?.Cancellation.Cancel();
    }

    private async Task RunAsync(Guid captureId, Request request, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await _delay(delay, request.Cancellation.Token).ConfigureAwait(false);
            }
            request.Cancellation.Token.ThrowIfCancellationRequested();
            await _onStable(
                captureId,
                request.Prompt,
                request.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (_requests.TryGetValue(captureId, out var current)
                    && ReferenceEquals(current, request))
                {
                    _requests.Remove(captureId);
                }
            }
            request.Cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Request[] requests;
        lock (_sync)
        {
            requests = _requests.Values.ToArray();
            _requests.Clear();
        }
        foreach (var request in requests)
        {
            request.Cancellation.Cancel();
        }
    }

    private sealed record Request(string Prompt, CancellationTokenSource Cancellation);
}
