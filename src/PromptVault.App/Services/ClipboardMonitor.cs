using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PromptVault.Core;
using DataFormats = System.Windows.DataFormats;

namespace PromptVault.App.Services;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly TimeSpan AiSummaryVisibility = TimeSpan.FromSeconds(5);
    private static readonly string[] SupportedImageExtensions =
        [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    private readonly Window _owner;
    private readonly LibraryRepository _repository;
    private readonly CaptureCoordinator _coordinator;
    private readonly Func<IReadOnlyList<CategoryRecord>> _categories;
    private readonly Func<bool> _quickEditEnabled;
    private readonly Func<PendingCapture, string, string, long?, string, Task<CaptureSaveResult>> _save;
    private readonly Func<Task> _libraryChanged;
    private readonly SequentialEventPump<ClipboardSnapshot> _eventPump;
    private readonly CapturePromptDebouncer _promptDebouncer;
    private readonly ClipboardFileEventCoalescer _fileEventCoalescer = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, PendingCapture> _pending = [];
    private readonly Dictionary<Guid, CaptureSessionRecord> _sessions = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _promptDeadlines = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _capsuleExpirations = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _aiSummaryExpirations = [];

    private HwndSource? _source;
    private CaptureCapsuleWindow? _capsule;
    private CaptureInboxWindow? _inboxWindow;
    private CaptureWindow? _captureWindow;
    private AiReviewWindow? _reviewWindow;
    private Guid? _expandedCaptureId;
    private uint _lastSequence;
    private bool _enabled = true;
    private int _disposed;

    public bool IsEnabled => _enabled;

    public ClipboardMonitor(
        Window owner,
        LibraryRepository repository,
        CaptureCoordinator coordinator,
        Func<IReadOnlyList<CategoryRecord>> categories,
        Func<bool> quickEditEnabled,
        Func<PendingCapture, string, string, long?, string, Task<CaptureSaveResult>> save,
        Func<Task> libraryChanged)
    {
        _owner = owner;
        _repository = repository;
        _coordinator = coordinator;
        _categories = categories;
        _quickEditEnabled = quickEditEnabled;
        _save = save;
        _libraryChanged = libraryChanged;
        _eventPump = new SequentialEventPump<ClipboardSnapshot>(
            HandleClipboardSnapshotAsync,
            ex => _owner.Dispatcher.BeginInvoke(() =>
                ToastService.Show(_owner, FriendlyReadError(ex, "无法处理剪贴板内容"))));
        _promptDebouncer = new CapturePromptDebouncer(
            (captureId, _, cancellationToken) =>
                RunSerializedAsync(
                    () => AutoSaveAsync(captureId, cancellationToken),
                    "自动保存失败",
                    cancellationToken: cancellationToken));
        owner.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        if (!AddClipboardFormatListener(handle))
        {
            throw new InvalidOperationException(
                $"无法启动剪贴板监听，Windows 错误码：{Marshal.GetLastWin32Error()}。");
        }
        _ = RecoverPersistedSessionsAsync();
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WmClipboardUpdate || !_enabled || Volatile.Read(ref _disposed) != 0)
        {
            return IntPtr.Zero;
        }

        var sequence = GetClipboardSequenceNumber();
        if (sequence != 0 && sequence == _lastSequence) return IntPtr.Zero;
        var observedAt = Stopwatch.GetTimestamp();
        _lastSequence = sequence;
        _ = _owner.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () => CaptureAndEnqueueClipboard(sequence, observedAt));

        return IntPtr.Zero;
    }

    private void ShowImageDetectedFeedback(uint sequence, long observedTimestamp)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
        EnsureCapsule().ShowDetected();
        var elapsed = Stopwatch.GetElapsedTime(observedTimestamp).TotalMilliseconds;
        DevelopmentPerformanceTrace.Event("capture-feedback-visible", new
        {
            Sequence = sequence,
            elapsedMs = elapsed
        });
    }

    private void CaptureAndEnqueueClipboard(uint sequence, long observedAt)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
        if (sequence != 0 && GetClipboardSequenceNumber() != sequence) return;
        var read = TryCaptureClipboardSnapshot(sequence, observedAt);
        if (read.Snapshot is { } snapshot)
        {
            _eventPump.TryEnqueue(snapshot);
            DevelopmentPerformanceTrace.Event("clipboard-event-enqueued", new
            {
                sequence,
                hasImage = snapshot.HasImage,
                hasText = !string.IsNullOrWhiteSpace(snapshot.Text)
            });
        }
        else if (read.WasBusy)
        {
            _ = RetryClipboardReadAsync(sequence, observedAt);
        }
    }

    private async Task RetryClipboardReadAsync(uint expectedSequence, long observedAt)
    {
        var token = _lifetime.Token;
        try
        {
            foreach (var delay in new[] { 20, 50, 100 })
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
                var currentSequence = GetClipboardSequenceNumber();
                if (expectedSequence != 0 && currentSequence != expectedSequence) return;
                var read = await _owner.Dispatcher.InvokeAsync(() =>
                {
                    return TryCaptureClipboardSnapshot(currentSequence, observedAt);
                });
                if (read.Snapshot is not { } snapshot)
                {
                    if (!read.WasBusy) return;
                    continue;
                }

                _lastSequence = currentSequence;
                _eventPump.TryEnqueue(snapshot);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task HandleClipboardSnapshotAsync(
        ClipboardSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_enabled || Volatile.Read(ref _disposed) != 0) return;
            if (snapshot.HasImage)
            {
                if (snapshot.FilePath is { } filePath
                    && !_fileEventCoalescer.ShouldAccept(filePath, snapshot.ObservedTimestamp))
                {
                    DevelopmentPerformanceTrace.Event(
                        "capture-duplicate-file-event-coalesced",
                        new { snapshot.Sequence, filePath });
                    return;
                }
                PendingCapture pending;
                try
                {
                    pending = snapshot.FilePath is not null
                        ? await _coordinator.CreateFromFileAsync(
                            snapshot.FilePath,
                            snapshot.Sequence,
                            cancellationToken).ConfigureAwait(false)
                        : await _coordinator.CreateFromBitmapAsync(
                            snapshot.Image!,
                            snapshot.Sequence,
                            cancellationToken).ConfigureAwait(false);
                }
                catch (CapturePreparationException ex)
                {
                    await TrackFailedSessionAsync(ex.CaptureId).ConfigureAwait(false);
                    throw;
                }
                if (Volatile.Read(ref _disposed) != 0) return;
                await TrackPendingAsync(pending, cancellationToken).ConfigureAwait(false);
                DevelopmentPerformanceTrace.Event("clipboard-image-ready", new
                {
                    snapshot.Sequence,
                    captureId = pending.SessionId
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(snapshot.Text)) return;
            await ApplyPromptToLatestAsync(
                snapshot.Text,
                snapshot.Sequence,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _owner.Dispatcher.InvokeAsync(() =>
                ToastService.Show(_owner, FriendlyReadError(ex, "无法读取剪贴板内容")));
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public Task CaptureDroppedImageAsync(DroppedImageSource source)
    {
        if (source.FilePath is not null) return CaptureFileAsync(source.FilePath);
        if (source.Uri is not null) return CaptureUriAsync(source.Uri);
        return Task.CompletedTask;
    }

    public async Task ShowAiSummaryAsync(Guid captureId, string summary)
    {
        await RunSerializedAsync(async () =>
        {
            var session = await _repository.SetCaptureAiSummaryAsync(
                captureId,
                summary,
                _lifetime.Token).ConfigureAwait(false);
            _sessions[captureId] = session;
            await RefreshCapsuleAsync(captureId).ConfigureAwait(false);
            ScheduleAiSummaryExpiration(captureId, AiSummaryVisibility);
        }).ConfigureAwait(false);
    }

    public async Task ShowAiReviewAsync(long? preferredItemId = null)
    {
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            if (_reviewWindow is { IsVisible: true })
            {
                _reviewWindow.Activate();
                return;
            }
            var window = new AiReviewWindow(_owner, _repository, preferredItemId);
            _reviewWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_reviewWindow, window)) _reviewWindow = null;
            };
            window.Show();
            window.Activate();
        });
    }

    private async Task CaptureFileAsync(string path)
    {
        await RunSerializedAsync(async () =>
        {
            await _owner.Dispatcher.InvokeAsync(() => EnsureCapsule().ShowDetected());
            PendingCapture pending;
            try
            {
                pending = await _coordinator.CreateFromFileAsync(
                    path,
                    _lifetime.Token).ConfigureAwait(false);
            }
            catch (CapturePreparationException ex)
            {
                await TrackFailedSessionAsync(ex.CaptureId).ConfigureAwait(false);
                throw;
            }
            await TrackPendingAsync(pending, _lifetime.Token).ConfigureAwait(false);
        }, "无法读取拖入的图片").ConfigureAwait(false);
    }

    private async Task CaptureUriAsync(Uri uri)
    {
        await RunSerializedAsync(async () =>
        {
            await _owner.Dispatcher.InvokeAsync(() => EnsureCapsule().ShowDetected());
            PendingCapture pending;
            try
            {
                pending = await _coordinator.CreateFromUriAsync(
                    uri,
                    _lifetime.Token).ConfigureAwait(false);
            }
            catch (CapturePreparationException ex)
            {
                await TrackFailedSessionAsync(ex.CaptureId).ConfigureAwait(false);
                throw;
            }
            await TrackPendingAsync(pending, _lifetime.Token).ConfigureAwait(false);
        }, "无法读取拖入的图片链接").ConfigureAwait(false);
    }

    private async Task TrackFailedSessionAsync(Guid captureId)
    {
        var session = await _repository.GetCaptureSessionAsync(
            captureId,
            _lifetime.Token).ConfigureAwait(false);
        if (session is null) return;
        _sessions[captureId] = session;
        await RefreshCapsuleAsync(captureId).ConfigureAwait(false);
        await RefreshInboxWindowAsync().ConfigureAwait(false);
    }

    private async Task TrackPendingAsync(
        PendingCapture pending,
        CancellationToken cancellationToken)
    {
        var duplicatePending = _pending.Values
            .Where(existing =>
                existing.SessionId != pending.SessionId
                && string.Equals(existing.Hash, pending.Hash, StringComparison.Ordinal)
                && Math.Abs((existing.CapturedAt - pending.CapturedAt).TotalSeconds) <= 2)
            .OrderByDescending(existing => existing.CapturedAt)
            .FirstOrDefault();
        if (duplicatePending is not null)
        {
            var duplicateSession = await _repository.GetCaptureSessionAsync(
                pending.SessionId,
                cancellationToken).ConfigureAwait(false);
            if (duplicateSession is not null)
            {
                await _coordinator.DiscardAsync(
                    duplicateSession,
                    pending,
                    cancellationToken).ConfigureAwait(false);
            }
            DevelopmentPerformanceTrace.Event("capture-duplicate-event-coalesced", new
            {
                keptCaptureId = duplicatePending.SessionId,
                discardedCaptureId = pending.SessionId,
                pending.Hash
            });
            await RefreshCapsuleAsync(duplicatePending.SessionId).ConfigureAwait(false);
            return;
        }

        _pending[pending.SessionId] = pending;
        var session = await _repository.GetCaptureSessionAsync(
            pending.SessionId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("图片已经准备完成，但捕获状态不存在。");
        if (pending.ExistingItem is { } existing && string.IsNullOrWhiteSpace(session.Notes))
        {
            session = await _repository.UpdateCaptureDraftAsync(
                session.Id,
                session.Prompt,
                existing.Notes,
                existing.CategoryId,
                existing.Tags,
                cancellationToken).ConfigureAwait(false);
        }
        _sessions[session.Id] = session;
        if (session.State == CaptureState.WaitingForPrompt)
        {
            SchedulePromptDeadline(session);
        }
        else if (session.State == CaptureState.NeedsPrompt)
        {
            CancelTimer(_promptDeadlines, session.Id);
        }
        await RefreshCapsuleAsync(session.Id).ConfigureAwait(false);
        if (_quickEditEnabled())
        {
            await _owner.Dispatcher.InvokeAsync(() =>
                OpenCaptureWindowCore(session.Id, session, pending, activate: true));
        }
    }

    private async Task ApplyPromptToLatestAsync(
        string prompt,
        uint sequence,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var target = CapturePromptRouter.SelectLatestWaiting(
            _sessions.Values,
            now,
            _pending.ContainsKey);
        if (target is null) return;

        var updated = await _repository.SetCapturePromptAsync(
            target.Id,
            prompt,
            sequence,
            cancellationToken).ConfigureAwait(false);
        _sessions[target.Id] = updated;
        CancelTimer(_promptDeadlines, target.Id);
        SchedulePromptDebounce(updated, CapturePromptDebouncer.DefaultDelay);
        if (_expandedCaptureId == target.Id)
        {
            await _owner.Dispatcher.InvokeAsync(() => _captureWindow?.SetPrompt(updated.Prompt));
        }
        await RefreshCapsuleAsync(target.Id).ConfigureAwait(false);
        DevelopmentPerformanceTrace.Event("clipboard-prompt-applied", new
        {
            sequence,
            captureId = target.Id,
            debounceMs = CapturePromptDebouncer.DefaultDelay.TotalMilliseconds
        });
    }

    private async Task RecoverPersistedSessionsAsync()
    {
        await RunSerializedAsync(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var sessions = await _repository.GetActiveCaptureSessionsAsync(
                now,
                _lifetime.Token).ConfigureAwait(false);
            foreach (var original in sessions)
            {
                var session = original;
                if (session.State == CaptureState.WaitingForPrompt
                    && session.PromptDeadlineAt <= now)
                {
                    session = await _repository.TransitionCaptureAsync(
                        session.Id,
                        CaptureState.NeedsPrompt,
                        cancellationToken: _lifetime.Token).ConfigureAwait(false);
                }

                _sessions[session.Id] = session;
                if (CaptureStateMachine.IsRecoverable(session.State))
                {
                    try
                    {
                        if (await _coordinator.RecoverAsync(
                                session,
                                _lifetime.Token).ConfigureAwait(false) is { } pending)
                        {
                            _pending[session.Id] = pending;
                            session = await _repository.GetCaptureSessionAsync(
                                session.Id,
                                _lifetime.Token).ConfigureAwait(false) ?? session;
                            _sessions[session.Id] = session;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning(
                            "capture-recovery",
                            $"Capture session {session.Id:D} could not be rehydrated.",
                            ex);
                        session = await _repository.GetCaptureSessionAsync(
                            session.Id,
                            _lifetime.Token).ConfigureAwait(false) ?? session;
                        _sessions[session.Id] = session;
                    }
                }

                if (session.State == CaptureState.WaitingForPrompt)
                {
                    SchedulePromptDeadline(session);
                }
                else if (session.State == CaptureState.PromptDebouncing
                         && !string.IsNullOrWhiteSpace(session.Prompt)
                         && _pending.ContainsKey(session.Id))
                {
                    var remaining = CapturePromptDebouncer.DefaultDelay - (now - session.UpdatedAt);
                    SchedulePromptDebounce(
                        session,
                        remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                }
                else if (session.State is CaptureState.Saved or CaptureState.WaitingForAi
                         && session.UndoDeadlineAt is { } undoDeadline)
                {
                    ScheduleCapsuleExpiration(session.Id, undoDeadline - now);
                }
            }
            await RefreshCapsuleAsync().ConfigureAwait(false);
            DevelopmentPerformanceTrace.Event("capture-recovery-complete", new
            {
                sessions = sessions.Count,
                pending = _pending.Count
            });
        }, "无法恢复未完成收录").ConfigureAwait(false);
    }

    private void SchedulePromptDebounce(
        CaptureSessionRecord session,
        TimeSpan delay)
    {
        _promptDebouncer.Submit(session.Id, session.Prompt, delay);
    }

    private void SchedulePromptDeadline(CaptureSessionRecord session)
    {
        CancelTimer(_promptDeadlines, session.Id);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _promptDeadlines[session.Id] = cancellation;
        _ = CompletePromptDeadlineAsync(
            session.Id,
            session.PromptDeadlineAt - DateTimeOffset.UtcNow,
            cancellation);
    }

    private async Task CompletePromptDeadlineAsync(
        Guid captureId,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }
            await RunSerializedAsync(async () =>
            {
                if (!_sessions.TryGetValue(captureId, out var session)
                    || session.State != CaptureState.WaitingForPrompt)
                {
                    return;
                }
                var updated = await _repository.TransitionCaptureAsync(
                    captureId,
                    CaptureState.NeedsPrompt,
                    cancellationToken: cancellation.Token).ConfigureAwait(false);
                _sessions[captureId] = updated;
                DevelopmentPerformanceTrace.Event("capture-needs-prompt", new
                {
                    captureId,
                    updated.CapturedAt,
                    updated.PromptDeadlineAt,
                    transitionedAt = updated.UpdatedAt,
                    elapsedMs = (updated.UpdatedAt - updated.CapturedAt).TotalMilliseconds
                });
                await RefreshCapsuleAsync(captureId).ConfigureAwait(false);
                await RefreshInboxWindowAsync().ConfigureAwait(false);
            },
                "无法更新待补提示词状态",
                cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_promptDeadlines.TryGetValue(captureId, out var current)
                && ReferenceEquals(current, cancellation))
            {
                RemoveTimerIfCurrent(_promptDeadlines, captureId, cancellation);
            }
            cancellation.Dispose();
        }
    }

    private async Task AutoSaveAsync(Guid captureId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(captureId, out var session)
            || session.State != CaptureState.PromptDebouncing
            || string.IsNullOrWhiteSpace(session.Prompt)
            || !_pending.TryGetValue(captureId, out var pending))
        {
            return;
        }
        await SaveCaptureAsync(
            session,
            pending,
            session.Prompt,
            session.Notes,
            session.CategoryId,
            session.Tags,
            automatic: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveManualAsync(
        Guid captureId,
        string prompt,
        string notes,
        long? category,
        string tags)
    {
        await RunSerializedAsync(async () =>
        {
            await SaveManualUnderGateAsync(
                captureId,
                prompt,
                notes,
                category,
                tags).ConfigureAwait(false);
        }, "保存失败", showToast: false).ConfigureAwait(false);
    }

    private async Task SaveManualUnderGateAsync(
        Guid captureId,
        string prompt,
        string notes,
        long? category,
        string tags)
    {
        if (!_sessions.TryGetValue(captureId, out var session)
            || !_pending.TryGetValue(captureId, out var pending))
        {
            throw new InvalidOperationException("待补图片的暂存文件不可用。");
        }
        session = await _repository.UpdateCaptureDraftAsync(
            captureId,
            prompt,
            notes,
            category,
            tags,
            _lifetime.Token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(prompt)
            && session.State != CaptureState.PromptDebouncing)
        {
            session = await _repository.SetCapturePromptAsync(
                captureId,
                prompt,
                cancellationToken: _lifetime.Token).ConfigureAwait(false);
        }
        _sessions[captureId] = session;
        _promptDebouncer.Cancel(captureId);
        CancelTimer(_promptDeadlines, captureId);
        await SaveCaptureAsync(
            session,
            pending,
            prompt,
            notes,
            category,
            tags,
            automatic: false,
            _lifetime.Token).ConfigureAwait(false);
    }

    private async Task SaveCaptureAsync(
        CaptureSessionRecord session,
        PendingCapture pending,
        string prompt,
        string notes,
        long? category,
        string tags,
        bool automatic,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _save(
                pending,
                prompt,
                notes,
                category,
                tags).ConfigureAwait(false);
            _pending.Remove(session.Id);
            CancelTimer(_promptDeadlines, session.Id);
            var saved = await _repository.GetCaptureSessionAsync(
                session.Id,
                _lifetime.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("图片已经保存，但捕获状态不存在。");
            _sessions[session.Id] = saved;
            await CloseExpandedWindowAsync(session.Id).ConfigureAwait(false);
            await RefreshCapsuleAsync(session.Id).ConfigureAwait(false);
            ScheduleCapsuleExpiration(
                session.Id,
                result.UndoDeadlineAt - DateTimeOffset.UtcNow);
            DevelopmentPerformanceTrace.Event(
                automatic ? "capture-auto-saved" : "capture-manual-saved",
                new
                {
                    captureId = session.Id,
                    result.ItemId,
                    result.WasDuplicate,
                    debounceMs = CapturePromptDebouncer.DefaultDelay.TotalMilliseconds
                });
            await RefreshInboxWindowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failed = await TryMarkFailedAsync(
                session.Id,
                FriendlyReadError(ex, "保存失败"),
                cancellationToken).ConfigureAwait(false);
            if (failed is not null) _sessions[session.Id] = failed;
            await _owner.Dispatcher.InvokeAsync(() =>
            {
                _captureWindow?.ShowError(FriendlyReadError(ex, "保存失败"));
            });
            await RefreshCapsuleAsync(session.Id).ConfigureAwait(false);
        }
    }

    private void ScheduleCapsuleExpiration(Guid captureId, TimeSpan delay)
    {
        CancelTimer(_capsuleExpirations, captureId);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _capsuleExpirations[captureId] = cancellation;
        _ = ExpireCapsuleSessionAsync(captureId, delay, cancellation);
    }

    private void ScheduleAiSummaryExpiration(Guid captureId, TimeSpan delay)
    {
        CancelTimer(_aiSummaryExpirations, captureId);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _aiSummaryExpirations[captureId] = cancellation;
        _ = ExpireAiSummaryAsync(captureId, delay, cancellation);
    }

    private async Task ExpireAiSummaryAsync(
        Guid captureId,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }
            await RunSerializedAsync(async () =>
            {
                if (!_sessions.TryGetValue(captureId, out var session)
                    || string.IsNullOrWhiteSpace(session.AiSummary))
                {
                    return;
                }
                var updated = await _repository.ClearCaptureAiSummaryAsync(
                    captureId,
                    cancellation.Token).ConfigureAwait(false);
                _sessions[captureId] = updated;
                await RefreshCapsuleAsync(captureId).ConfigureAwait(false);
            },
                showToast: false,
                cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_aiSummaryExpirations.TryGetValue(captureId, out var current)
                && ReferenceEquals(current, cancellation))
            {
                RemoveTimerIfCurrent(_aiSummaryExpirations, captureId, cancellation);
            }
            cancellation.Dispose();
        }
    }

    private async Task ExpireCapsuleSessionAsync(
        Guid captureId,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }
            await RunSerializedAsync(
                async () =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    _sessions.Remove(captureId);
                    await RefreshCapsuleAsync().ConfigureAwait(false);
                },
                showToast: false,
                cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_capsuleExpirations.TryGetValue(captureId, out var current)
                && ReferenceEquals(current, cancellation))
            {
                RemoveTimerIfCurrent(_capsuleExpirations, captureId, cancellation);
            }
            cancellation.Dispose();
        }
    }

    private async Task ExpandCaptureAsync(Guid captureId)
    {
        await RunSerializedAsync(async () =>
        {
            if (!_sessions.TryGetValue(captureId, out var session)
                || !_pending.TryGetValue(captureId, out var pending))
            {
                await _owner.Dispatcher.InvokeAsync(() =>
                    ToastService.Show(_owner, session?.Error ?? "这条收录暂时无法展开。"));
                return;
            }

            await _owner.Dispatcher.InvokeAsync(() =>
                OpenCaptureWindowCore(captureId, session, pending, activate: true));
        }).ConfigureAwait(false);
    }

    private void OpenCaptureWindowCore(
        Guid captureId,
        CaptureSessionRecord session,
        PendingCapture pending,
        bool activate)
    {
        if (_captureWindow is { IsVisible: true }
            && _expandedCaptureId == captureId)
        {
            if (activate) _captureWindow.ActivateForEditing();
            return;
        }
        CloseExpandedWindowCore();
        var window = new CaptureWindow(pending, _categories(), activate);
        _captureWindow = window;
        _expandedCaptureId = captureId;
        window.SetDraft(session);
        window.SaveRequested += (prompt, notes, category, tags) =>
            SaveManualAsync(captureId, prompt, notes, category, tags);
        window.DraftChanged += (prompt, notes, category, tags) =>
            PersistCaptureDraftAsync(captureId, prompt, notes, category, tags);
        window.CloseRequested += () =>
        {
            if (ReferenceEquals(_captureWindow, window))
            {
                _captureWindow = null;
                _expandedCaptureId = null;
            }
        };
        window.DeleteRequested += () => _ = DeleteCaptureAsync(captureId);
        window.ImageDropped += CaptureDroppedImageAsync;
        window.Show();
        if (activate) window.ActivateForEditing();
    }

    private Task PersistCaptureDraftAsync(
        Guid captureId,
        string prompt,
        string notes,
        long? category,
        string tags) =>
        RunSerializedAsync(async () =>
        {
            if (!_sessions.ContainsKey(captureId)) return;
            var session = await _repository.UpdateCaptureDraftAsync(
                captureId,
                prompt,
                notes,
                category,
                tags,
                _lifetime.Token).ConfigureAwait(false);
            _sessions[captureId] = session;
        }, "保存快速标注草稿失败", showToast: false);

    private async Task UndoCaptureAsync(Guid captureId)
    {
        await RunSerializedAsync(async () =>
        {
            CancelTimer(_capsuleExpirations, captureId);
            CancelTimer(_aiSummaryExpirations, captureId);
            var result = await _repository.UndoCaptureAsync(
                captureId,
                DateTimeOffset.UtcNow,
                _lifetime.Token).ConfigureAwait(false);
            var undone = await _repository.GetCaptureSessionAsync(
                captureId,
                _lifetime.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("撤销后找不到捕获记录。");
            _sessions[captureId] = undone;
            await _libraryChanged().ConfigureAwait(false);
            await RefreshCapsuleAsync(captureId).ConfigureAwait(false);
            ScheduleCapsuleExpiration(captureId, TimeSpan.FromSeconds(2));
            if (result.FileDeletionFailures.Count > 0)
            {
                await _owner.Dispatcher.InvokeAsync(() =>
                    ToastService.Show(_owner, "收录已撤销，但有图片文件需要稍后清理。"));
            }
        }, "撤销收录失败").ConfigureAwait(false);
    }

    private async Task DeleteCaptureAsync(Guid captureId)
    {
        await RunSerializedAsync(async () =>
        {
            if (!_sessions.TryGetValue(captureId, out var session)) return;
            _pending.Remove(captureId, out var pending);
            await _coordinator.DiscardAsync(
                session,
                pending,
                _lifetime.Token).ConfigureAwait(false);
            _sessions.Remove(captureId);
            _fileEventCoalescer.Reset();
            _lastSequence = 0;
            _promptDebouncer.Cancel(captureId);
            CancelTimer(_promptDeadlines, captureId);
            CancelTimer(_capsuleExpirations, captureId);
            CancelTimer(_aiSummaryExpirations, captureId);
            await CloseExpandedWindowAsync(captureId).ConfigureAwait(false);
            await RefreshCapsuleAsync().ConfigureAwait(false);
            await RefreshInboxWindowAsync().ConfigureAwait(false);
        }, "删除待补图片失败").ConfigureAwait(false);
    }

    private async Task<CaptureSessionRecord?> TryMarkFailedAsync(
        Guid captureId,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _repository.GetCaptureSessionAsync(
                captureId,
                cancellationToken).ConfigureAwait(false);
            if (current is null || current.State == CaptureState.Failed) return current;
            return await _repository.TransitionCaptureAsync(
                captureId,
                CaptureState.Failed,
                error,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception transitionError)
        {
            AppLog.Warning(
                "capture-state",
                "A capture save failure could not be persisted.",
                transitionError);
            return null;
        }
    }

    private CaptureCapsuleWindow EnsureCapsule()
    {
        if (_capsule is not null) return _capsule;
        _capsule = new CaptureCapsuleWindow();
        _capsule.ExpandRequested += captureId => _ = ExpandCaptureAsync(captureId);
        _capsule.UndoRequested += captureId => _ = UndoCaptureAsync(captureId);
        _capsule.ReviewRequested += itemId => _ = ShowAiReviewAsync(itemId);
        _capsule.InboxRequested += () => _ = ShowInboxAsync();
        return _capsule;
    }

    private async Task RefreshCapsuleAsync(Guid? preferredCaptureId = null)
    {
        var session = preferredCaptureId is { } preferred
                      && _sessions.TryGetValue(preferred, out var selected)
            ? selected
            : _sessions.Values
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefault();
        var inboxCount = _sessions.Values.Count(item =>
            item.State is CaptureState.NeedsPrompt or CaptureState.Failed);
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            if (session is null)
            {
                _capsule?.HideCapsule();
                return;
            }
            _pending.TryGetValue(session.Id, out var pending);
            EnsureCapsule().UpdateSession(session, pending?.Preview, inboxCount);
        });
    }

    private async Task ShowInboxAsync()
    {
        await RunSerializedAsync(async () =>
        {
            var items = await LoadInboxItemsAsync().ConfigureAwait(false);
            await _owner.Dispatcher.InvokeAsync(() =>
            {
                if (_inboxWindow is null)
                {
                    var window = new CaptureInboxWindow(_owner);
                    _inboxWindow = window;
                    window.SaveRequested += SaveInboxItemsAsync;
                    window.DeleteRequested += DeleteInboxItemsAsync;
                    window.RefreshRequested += RequestInboxRefreshAsync;
                    window.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_inboxWindow, window))
                        {
                            _inboxWindow = null;
                        }
                    };
                }
                _inboxWindow.ReplaceItems(items);
                if (!_inboxWindow.IsVisible) _inboxWindow.Show();
                _inboxWindow.Activate();
            });
        }, "无法打开待补提示词").ConfigureAwait(false);
    }

    private Task RequestInboxRefreshAsync() =>
        RunSerializedAsync(
            RefreshInboxWindowAsync,
            "刷新待补提示词失败",
            showToast: false);

    private async Task RefreshInboxWindowAsync()
    {
        if (_inboxWindow is null) return;
        var items = await LoadInboxItemsAsync().ConfigureAwait(false);
        await _owner.Dispatcher.InvokeAsync(() => _inboxWindow?.ReplaceItems(items));
    }

    private async Task<IReadOnlyList<CaptureInboxItemViewModel>> LoadInboxItemsAsync()
    {
        var sessions = await _repository.GetCaptureInboxAsync(
            _lifetime.Token).ConfigureAwait(false);
        var items = new List<CaptureInboxItemViewModel>(sessions.Count);
        foreach (var original in sessions)
        {
            var session = original;
            _sessions[session.Id] = session;
            if (!_pending.ContainsKey(session.Id) && CaptureStateMachine.IsRecoverable(session.State))
            {
                try
                {
                    if (await _coordinator.RecoverAsync(
                            session,
                            _lifetime.Token).ConfigureAwait(false) is { } pending)
                    {
                        _pending[session.Id] = pending;
                        session = await _repository.GetCaptureSessionAsync(
                            session.Id,
                            _lifetime.Token).ConfigureAwait(false) ?? session;
                        _sessions[session.Id] = session;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning(
                        "capture-inbox-recovery",
                        $"Capture inbox session {session.Id:D} could not be rehydrated.",
                        ex);
                    session = await _repository.GetCaptureSessionAsync(
                        session.Id,
                        _lifetime.Token).ConfigureAwait(false) ?? session;
                    _sessions[session.Id] = session;
                }
            }
            _pending.TryGetValue(session.Id, out var pendingCapture);
            items.Add(new CaptureInboxItemViewModel(
                session,
                pendingCapture?.Preview,
                pendingCapture is not null));
        }
        return items;
    }

    private Task SaveInboxItemsAsync(IReadOnlyList<CaptureInboxSaveRequest> requests) =>
        RunSerializedAsync(async () =>
        {
            foreach (var request in requests)
            {
                try
                {
                    if (!_sessions.TryGetValue(request.CaptureId, out var session))
                    {
                        session = await _repository.GetCaptureSessionAsync(
                            request.CaptureId,
                            _lifetime.Token).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("待补图片状态不存在。");
                        _sessions[request.CaptureId] = session;
                    }
                    await SaveManualUnderGateAsync(
                        request.CaptureId,
                        request.Prompt,
                        session.Notes,
                        session.CategoryId,
                        session.Tags).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var message = FriendlyReadError(ex, "保存失败");
                    await _owner.Dispatcher.InvokeAsync(() =>
                        _inboxWindow?.SetItemError(request.CaptureId, message));
                }
            }
            await RefreshInboxWindowAsync().ConfigureAwait(false);
            await RefreshCapsuleAsync().ConfigureAwait(false);
        }, "批量保存待补项目失败", showToast: false);

    private Task DeleteInboxItemsAsync(IReadOnlyList<Guid> captureIds) =>
        RunSerializedAsync(async () =>
        {
            foreach (var captureId in captureIds)
            {
                if (!_sessions.TryGetValue(captureId, out var session))
                {
                    session = await _repository.GetCaptureSessionAsync(
                        captureId,
                        _lifetime.Token).ConfigureAwait(false);
                }
                if (session is null) continue;
                _pending.Remove(captureId, out var pending);
                await _coordinator.DiscardAsync(
                    session,
                    pending,
                    _lifetime.Token).ConfigureAwait(false);
                _sessions.Remove(captureId);
                _promptDebouncer.Cancel(captureId);
                CancelTimer(_promptDeadlines, captureId);
                CancelTimer(_capsuleExpirations, captureId);
                await CloseExpandedWindowAsync(captureId).ConfigureAwait(false);
            }
            await RefreshInboxWindowAsync().ConfigureAwait(false);
            await RefreshCapsuleAsync().ConfigureAwait(false);
        }, "批量删除待补项目失败", showToast: false);

    private async Task CloseExpandedWindowAsync(Guid captureId)
    {
        if (_expandedCaptureId != captureId) return;
        await _owner.Dispatcher.InvokeAsync(CloseExpandedWindowCore);
    }

    private void CloseExpandedWindowCore()
    {
        var window = _captureWindow;
        _captureWindow = null;
        _expandedCaptureId = null;
        if (window?.IsVisible == true) window.CloseAfterSave();
    }

    private async Task RunSerializedAsync(
        Func<Task> action,
        string errorPrefix = "收录操作失败",
        bool showToast = true,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var entered = false;
        try
        {
            await _captureGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _disposed) != 0) return;
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warning("capture-workflow", errorPrefix, ex);
            if (showToast)
            {
                await _owner.Dispatcher.InvokeAsync(() =>
                    ToastService.Show(_owner, FriendlyReadError(ex, errorPrefix)));
            }
        }
        finally
        {
            if (entered) _captureGate.Release();
        }
    }

    private static void CancelTimer(
        ConcurrentDictionary<Guid, CancellationTokenSource> timers,
        Guid captureId)
    {
        if (!timers.TryRemove(captureId, out var cancellation)) return;
        cancellation.Cancel();
    }

    private static void RemoveTimerIfCurrent(
        ConcurrentDictionary<Guid, CancellationTokenSource> timers,
        Guid captureId,
        CancellationTokenSource cancellation) =>
        ((ICollection<KeyValuePair<Guid, CancellationTokenSource>>)timers).Remove(
            new KeyValuePair<Guid, CancellationTokenSource>(captureId, cancellation));

    private ClipboardReadResult TryCaptureClipboardSnapshot(
        uint sequence,
        long observedTimestamp)
    {
        try
        {
            var snapshot = ReadClipboardSnapshot(Clipboard.GetDataObject(), sequence, observedTimestamp, () =>
            {
                if (sequence == 0 || GetClipboardSequenceNumber() == sequence)
                    ShowImageDetectedFeedback(sequence, observedTimestamp);
            });
            if (sequence != 0 && GetClipboardSequenceNumber() != sequence) snapshot = null;
            return new ClipboardReadResult(snapshot, false);
        }
        catch (ExternalException)
        {
            return new ClipboardReadResult(null, true);
        }
    }

    internal static ClipboardSnapshot? ReadClipboardSnapshot(System.Windows.IDataObject? data, uint sequence, long observedTimestamp,
        Action? imageDetected = null)
    {
        if (data is null) return null;
        // File copies own the whole event, including any bitmap/text fallback formats.
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files) return null;
            var file = files[0];
            if (!File.Exists(file) || !SupportedImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) return null;
            imageDetected?.Invoke();
            return new ClipboardSnapshot(sequence, file, null, null, observedTimestamp);
        }
        var hasBitmap = data.GetDataPresent(DataFormats.Bitmap);
        if (hasBitmap) imageDetected?.Invoke();
        var image = hasBitmap ? data.GetData(DataFormats.Bitmap) as BitmapSource : null;
        image?.Freeze();
        var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
        if (image is null && string.IsNullOrWhiteSpace(text)) return null;
        return new ClipboardSnapshot(sequence, null, image, text, observedTimestamp);
    }

    private static string FriendlyReadError(Exception ex, string prefix)
    {
        if (ex is NotSupportedException) return $"{prefix}：{ex.Message}";
        if (ex is InvalidDataException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return $"{prefix}：{ex.Message}";
        }
        if (ex is HttpRequestException or TaskCanceledException)
        {
            return $"{prefix}：图片链接下载失败，请稍后再试。";
        }
        return $"{prefix}：{ex.Message}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.SourceInitialized -= OnSourceInitialized;
        if (_source is not null)
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
        }
        _eventPump.Stop();
        _lifetime.Cancel();
        _promptDebouncer.Dispose();
        foreach (var timer in _promptDeadlines.Values
                     .Concat(_capsuleExpirations.Values)
                     .Concat(_aiSummaryExpirations.Values)
                     .Distinct())
        {
            timer.Cancel();
        }
        _promptDeadlines.Clear();
        _capsuleExpirations.Clear();
        _aiSummaryExpirations.Clear();
        CloseExpandedWindowCore();
        _inboxWindow?.Close();
        _inboxWindow = null;
        _reviewWindow?.Close();
        _reviewWindow = null;
        _capsule?.Close();
        _capsule = null;
        _lifetime.Dispose();
    }

    internal sealed record ClipboardSnapshot(
        uint Sequence,
        string? FilePath,
        BitmapSource? Image,
        string? Text,
        long ObservedTimestamp)
    {
        public bool HasImage => FilePath is not null || Image is not null;
    }

    private sealed record ClipboardReadResult(ClipboardSnapshot? Snapshot, bool WasBusy);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

}
