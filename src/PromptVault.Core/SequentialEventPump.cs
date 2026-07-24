using System.Threading.Channels;

namespace PromptVault.Core;

internal sealed class SequentialEventPump<T>
{
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<T, CancellationToken, Task> _handler;
    private readonly Action<Exception>? _onError;
    private readonly Task _runner;
    private int _stopped;

    public SequentialEventPump(
        Func<T, CancellationToken, Task> handler,
        Action<Exception>? onError = null)
    {
        _handler = handler;
        _onError = onError;
        _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _runner = RunAsync();
    }

    public Task Completion => _runner;

    public bool TryEnqueue(T item) =>
        Volatile.Read(ref _stopped) == 0 && _channel.Writer.TryWrite(item);

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cancellation.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cancellation.Token))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    try
                    {
                        await _handler(item, _cancellation.Token);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _onError?.Invoke(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
