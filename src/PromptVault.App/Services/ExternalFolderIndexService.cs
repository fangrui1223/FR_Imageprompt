using System.Threading.Channels;
using System.Windows.Media.Imaging;
using PromptVault.Core;

namespace PromptVault.App.Services;

internal sealed record ExternalImageMetadata(int Width, int Height, string Format);

internal interface IExternalImageMetadataReader
{
    ExternalImageMetadata Read(string path);
}

internal sealed class WpfExternalImageMetadataReader : IExternalImageMetadataReader
{
    public ExternalImageMetadata Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("图片没有可读取的画面。");
            }

            var frame = decoder.Frames[0];
            return new ExternalImageMetadata(
                Math.Max(1, frame.PixelWidth),
                Math.Max(1, frame.PixelHeight),
                Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            var frame = ImagePipeline.DecodeFirstFrame(path);
            return new ExternalImageMetadata(
                Math.Max(1, frame.PixelWidth),
                Math.Max(1, frame.PixelHeight),
                Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
        }
    }
}

internal sealed record ExternalFolderIndexChangedEventArgs(
    string FolderId,
    ExternalFolderIndexState? State,
    bool ContentChanged);

internal sealed class ExternalFolderIndexService : IDisposable
{
    internal static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
    };

    private const int BatchSize = 256;
    private static readonly TimeSpan DefaultValidationInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultWatcherDebounce = TimeSpan.FromMilliseconds(250);
    private readonly LibraryRepository _repository;
    private readonly IExternalImageMetadataReader _metadataReader;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _validationInterval;
    private readonly TimeSpan _watcherDebounce;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ExternalFileChange> _changes =
        Channel.CreateUnbounded<ExternalFileChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly object _gate = new();
    private readonly Dictionary<string, ExternalFolderSetting> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ExternalFolderIndexState?>> _scanTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _folderCancellations = new(StringComparer.Ordinal);
    private readonly Task _changePump;
    private readonly Task _validationPump;
    private int _disposed;

    public ExternalFolderIndexService(
        LibraryRepository repository,
        IExternalImageMetadataReader? metadataReader = null,
        TimeProvider? timeProvider = null,
        TimeSpan? validationInterval = null,
        TimeSpan? watcherDebounce = null)
    {
        _repository = repository;
        _metadataReader = metadataReader ?? new WpfExternalImageMetadataReader();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _validationInterval = validationInterval ?? DefaultValidationInterval;
        _watcherDebounce = watcherDebounce ?? DefaultWatcherDebounce;
        _changePump = ProcessChangesAsync();
        _validationPump = RunValidationPumpAsync();
    }

    public event EventHandler<ExternalFolderIndexChangedEventArgs>? IndexChanged;

    public void Start(IEnumerable<ExternalFolderSetting> folders)
    {
        foreach (var folder in folders)
        {
            RegisterFolder(folder);
            _ = EnsureFolderIndexedInBackgroundAsync(folder);
        }
    }

    public void RegisterFolder(ExternalFolderSetting folder)
    {
        ThrowIfDisposed();
        var snapshot = Snapshot(folder);
        FileSystemWatcher? previousWatcher = null;
        lock (_gate)
        {
            if (_folders.TryGetValue(snapshot.Id, out var current)
                && string.Equals(current.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase)
                && _watchers.ContainsKey(snapshot.Id))
            {
                _folders[snapshot.Id] = snapshot;
                return;
            }
            if (!_folderCancellations.ContainsKey(snapshot.Id))
            {
                _folderCancellations[snapshot.Id] = new CancellationTokenSource();
            }
            _folders[snapshot.Id] = snapshot;
            if (_watchers.Remove(snapshot.Id, out var existing))
            {
                previousWatcher = existing;
            }
        }
        previousWatcher?.Dispose();
        TryCreateWatcher(snapshot);
    }

    public async Task<ExternalFolderIndexState?> EnsureFolderIndexedAsync(
        ExternalFolderSetting folder,
        bool forceValidation = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RegisterFolder(folder);
        var snapshot = Snapshot(folder);
        if (!forceValidation)
        {
            var state = await _repository.GetExternalFolderIndexStateAsync(
                snapshot.Id,
                cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (state is
                {
                    Status: ExternalFolderIndexStatus.Ready,
                    NextValidationAt: { } next
                }
                && string.Equals(state.RootPath, snapshot.Path, StringComparison.OrdinalIgnoreCase)
                && next > now)
            {
                return state;
            }
        }

        Task<ExternalFolderIndexState?> scanTask;
        lock (_gate)
        {
            if (!_scanTasks.TryGetValue(snapshot.Id, out scanTask!))
            {
                var folderCancellation = _folderCancellations[snapshot.Id];
                scanTask = Task.Run(
                    () => ScanFolderWithCancellationAsync(snapshot, folderCancellation.Token));
                _scanTasks[snapshot.Id] = scanTask;
                _ = RemoveCompletedScanAsync(snapshot.Id, scanTask);
            }
        }

        return await scanTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ExternalFolderIndexState?> GetStateAsync(
        string folderId,
        CancellationToken cancellationToken = default) =>
        _repository.GetExternalFolderIndexStateAsync(folderId, cancellationToken);

    public async Task RemoveFolderAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        FileSystemWatcher? watcher = null;
        CancellationTokenSource? folderCancellation = null;
        Task<ExternalFolderIndexState?>? scanTask = null;
        lock (_gate)
        {
            _folders.Remove(folderId);
            if (_watchers.Remove(folderId, out var existing)) watcher = existing;
            if (_folderCancellations.Remove(folderId, out var cancellation))
            {
                folderCancellation = cancellation;
            }
            if (_scanTasks.Remove(folderId, out var scanning))
            {
                scanTask = scanning;
            }
        }
        watcher?.Dispose();
        folderCancellation?.Cancel();
        if (scanTask is not null)
        {
            try
            {
                await scanTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _repository.RemoveExternalFolderIndexAsync(folderId, cancellationToken).ConfigureAwait(false);
        folderCancellation?.Dispose();
        RaiseChanged(folderId, null, contentChanged: true);
    }

    internal async Task ProcessFileChangeAsync(
        string folderId,
        ExternalFileChangeKind kind,
        string path,
        string? oldPath = null,
        CancellationToken cancellationToken = default)
    {
        Task<ExternalFolderIndexState?>? scanTask;
        lock (_gate)
        {
            _scanTasks.TryGetValue(folderId, out scanTask);
        }
        if (scanTask is not null)
        {
            try
            {
                await scanTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Folder removal cancels the scan; the registration check below decides whether to continue.
            }
        }

        ExternalFolderSetting? folder;
        lock (_gate)
        {
            _folders.TryGetValue(folderId, out folder);
        }
        if (folder is null) return;

        var isSupported = IsSupportedImage(path);
        var oldIsSupported = oldPath is not null && IsSupportedImage(oldPath);
        var now = _timeProvider.GetUtcNow();
        if (kind == ExternalFileChangeKind.Deleted)
        {
            if (isSupported)
            {
                await _repository.MarkExternalFileUnavailableAsync(
                    folderId,
                    NormalizePath(path),
                    ExternalFileAvailability.Missing,
                    "文件已删除或移动。",
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (kind == ExternalFileChangeKind.Renamed)
        {
            if (isSupported && File.Exists(path))
            {
                var input = ReadFile(path);
                if (oldIsSupported)
                {
                    await _repository.RenameExternalFileAsync(
                        folderId,
                        folder.Path,
                        NormalizePath(oldPath!),
                        input,
                        now,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _repository.UpsertExternalFileAsync(
                        folderId,
                        folder.Path,
                        input,
                        now,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else if (oldIsSupported)
            {
                await _repository.MarkExternalFileUnavailableAsync(
                    folderId,
                    NormalizePath(oldPath!),
                    ExternalFileAvailability.Missing,
                    "文件已重命名为不支持的格式或移出文件夹。",
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (!isSupported || !File.Exists(path)) return;
        await _repository.UpsertExternalFileAsync(
            folderId,
            folder.Path,
            ReadFile(path),
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _changes.Writer.TryComplete();
        FileSystemWatcher[] watchers;
        CancellationTokenSource[] folderCancellations;
        lock (_gate)
        {
            watchers = _watchers.Values.ToArray();
            folderCancellations = _folderCancellations.Values.ToArray();
            _watchers.Clear();
            _folderCancellations.Clear();
            _folders.Clear();
        }
        foreach (var watcher in watchers) watcher.Dispose();
        foreach (var cancellation in folderCancellations)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _lifetime.Dispose();
    }

    private async Task<ExternalFolderIndexState?> ScanFolderWithCancellationAsync(
        ExternalFolderSetting folder,
        CancellationToken folderCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            folderCancellation);
        return await ScanFolderCoreAsync(folder, linked.Token).ConfigureAwait(false);
    }

    private async Task<ExternalFolderIndexState?> ScanFolderCoreAsync(
        ExternalFolderSetting folder,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!Directory.Exists(folder.Path))
        {
            return await RecordFolderFailureAsync(
                folder,
                ExternalFolderIndexStatus.Missing,
                "外部文件夹不存在或当前不可用。",
                now,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var generation = await _repository.BeginExternalFolderScanAsync(
                folder.Id,
                folder.Path,
                now,
                cancellationToken).ConfigureAwait(false);
            RaiseChanged(
                folder.Id,
                await _repository.GetExternalFolderIndexStateAsync(folder.Id, cancellationToken).ConfigureAwait(false),
                contentChanged: false);
            var fingerprints = await _repository.GetExternalFileFingerprintsAsync(
                folder.Id,
                cancellationToken).ConfigureAwait(false);
            var changed = new List<ExternalFileIndexInput>(BatchSize);
            var unchanged = new List<string>(BatchSize);
            foreach (var path in Directory.EnumerateFiles(folder.Path, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSupportedImage(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    var normalized = NormalizePath(path);
                    var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    if (fingerprints.TryGetValue(normalized, out var existing)
                        && existing.FileSize == info.Length
                        && existing.ModifiedAt.ToUniversalTime() == modifiedAt
                        && existing.Availability == ExternalFileAvailability.Available)
                    {
                        unchanged.Add(normalized);
                    }
                    else
                    {
                        changed.Add(ReadFile(path, info, normalized, modifiedAt));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    changed.Add(CreateFailedFile(path, ex));
                }

                if (changed.Count >= BatchSize || unchanged.Count >= BatchSize)
                {
                    await FlushScanBatchAsync(
                        folder.Id,
                        generation,
                        changed,
                        unchanged,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await FlushScanBatchAsync(
                folder.Id,
                generation,
                changed,
                unchanged,
                cancellationToken).ConfigureAwait(false);
            var completed = _timeProvider.GetUtcNow();
            await _repository.CompleteExternalFolderScanAsync(
                folder.Id,
                generation,
                completed,
                completed + _validationInterval,
                cancellationToken).ConfigureAwait(false);
            var state = await _repository.GetExternalFolderIndexStateAsync(
                folder.Id,
                cancellationToken).ConfigureAwait(false);
            RaiseChanged(folder.Id, state, contentChanged: true);
            return state;
        }
        catch (UnauthorizedAccessException ex)
        {
            return await RecordFolderFailureAsync(
                folder,
                ExternalFolderIndexStatus.PermissionDenied,
                $"没有权限读取外部文件夹：{ex.Message}",
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warning("external-folder-index", "External folder scan failed.", ex, new
            {
                folder.Id,
                folder.Path
            });
            return await RecordFolderFailureAsync(
                folder,
                ExternalFolderIndexStatus.Failed,
                $"外部文件夹索引失败：{ex.Message}",
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushScanBatchAsync(
        string folderId,
        long generation,
        List<ExternalFileIndexInput> changed,
        List<string> unchanged,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (changed.Count > 0)
        {
            await _repository.UpsertExternalFilesAsync(
                folderId,
                generation,
                changed.ToArray(),
                now,
                cancellationToken).ConfigureAwait(false);
            changed.Clear();
        }
        if (unchanged.Count > 0)
        {
            await _repository.TouchExternalFilesAsync(
                folderId,
                generation,
                unchanged.ToArray(),
                now,
                cancellationToken).ConfigureAwait(false);
            unchanged.Clear();
        }
    }

    private async Task<ExternalFolderIndexState?> RecordFolderFailureAsync(
        ExternalFolderSetting folder,
        ExternalFolderIndexStatus status,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _repository.SetExternalFolderIndexFailureAsync(
            folder.Id,
            folder.Path,
            status,
            message,
            now + TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        var state = await _repository.GetExternalFolderIndexStateAsync(
            folder.Id,
            cancellationToken).ConfigureAwait(false);
        RaiseChanged(folder.Id, state, contentChanged: false);
        return state;
    }

    private ExternalFileIndexInput ReadFile(string path)
    {
        var info = new FileInfo(path);
        return ReadFile(
            path,
            info,
            NormalizePath(path),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private ExternalFileIndexInput ReadFile(
        string path,
        FileInfo info,
        string normalizedPath,
        DateTimeOffset modifiedAt)
    {
        try
        {
            var metadata = _metadataReader.Read(path);
            return new ExternalFileIndexInput(
                Path.GetFullPath(path),
                normalizedPath,
                Path.GetFileName(path),
                info.Length,
                modifiedAt,
                metadata.Width,
                metadata.Height,
                metadata.Format,
                ExternalThumbnailStatus.SourceReady,
                ExternalFileAvailability.Available);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateFailedFile(path, ex, info.Length, modifiedAt, normalizedPath);
        }
    }

    private static ExternalFileIndexInput CreateFailedFile(
        string path,
        Exception exception,
        long fileSize = 0,
        DateTimeOffset? modifiedAt = null,
        string? normalizedPath = null)
    {
        var availability = exception is UnauthorizedAccessException
            ? ExternalFileAvailability.PermissionDenied
            : ExternalFileAvailability.Unreadable;
        return new ExternalFileIndexInput(
            Path.GetFullPath(path),
            normalizedPath ?? NormalizePath(path),
            Path.GetFileName(path),
            Math.Max(0, fileSize),
            modifiedAt ?? DateTimeOffset.UtcNow,
            0,
            0,
            Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
            ExternalThumbnailStatus.Failed,
            availability,
            exception.Message);
    }

    private void TryCreateWatcher(ExternalFolderSetting folder)
    {
        if (!Directory.Exists(folder.Path)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                Filter = "*.*",
                InternalBufferSize = 64 * 1024
            };
            watcher.Created += (_, args) => EnqueueChange(folder.Id, ExternalFileChangeKind.Changed, args.FullPath);
            watcher.Changed += (_, args) => EnqueueChange(folder.Id, ExternalFileChangeKind.Changed, args.FullPath);
            watcher.Deleted += (_, args) => EnqueueChange(folder.Id, ExternalFileChangeKind.Deleted, args.FullPath);
            watcher.Renamed += (_, args) => EnqueueChange(
                folder.Id,
                ExternalFileChangeKind.Renamed,
                args.FullPath,
                args.OldFullPath);
            watcher.Error += (_, args) =>
            {
                AppLog.Warning("external-folder-watcher", "External folder watcher requires a full validation.", args.GetException(), new
                {
                    folder.Id,
                    folder.Path
                });
                _ = EnsureFolderIndexedInBackgroundAsync(folder, forceValidation: true);
            };
            watcher.EnableRaisingEvents = true;
            lock (_gate)
            {
                if (_folders.ContainsKey(folder.Id))
                {
                    _watchers[folder.Id] = watcher;
                    watcher = null;
                }
            }
            watcher?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warning("external-folder-watcher", "External folder watcher could not be started.", ex, new
            {
                folder.Id,
                folder.Path
            });
        }
    }

    private void EnqueueChange(
        string folderId,
        ExternalFileChangeKind kind,
        string path,
        string? oldPath = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _changes.Writer.TryWrite(new ExternalFileChange(folderId, kind, path, oldPath));
    }

    private async Task ProcessChangesAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (await _changes.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                var batch = new List<ExternalFileChange>();
                var pendingChanges = new Dictionary<string, ExternalFileChange>(StringComparer.OrdinalIgnoreCase);
                void Collect(ExternalFileChange change)
                {
                    var key = ChangeKey(change);
                    if (change.Kind == ExternalFileChangeKind.Changed)
                    {
                        pendingChanges[key] = change;
                    }
                    else
                    {
                        pendingChanges.Remove(key);
                        batch.Add(change);
                    }
                }
                while (_changes.Reader.TryRead(out var change))
                {
                    Collect(change);
                }
                await Task.Delay(_watcherDebounce, token).ConfigureAwait(false);
                while (_changes.Reader.TryRead(out var change))
                {
                    Collect(change);
                }
                batch.AddRange(pendingChanges.Values);

                var affectedFolders = new HashSet<string>(StringComparer.Ordinal);
                foreach (var change in batch)
                {
                    try
                    {
                        await ProcessFileChangeAsync(
                            change.FolderId,
                            change.Kind,
                            change.Path,
                            change.OldPath,
                            token).ConfigureAwait(false);
                        affectedFolders.Add(change.FolderId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppLog.Warning("external-folder-change", "External folder change could not be indexed.", ex, new
                        {
                            change.FolderId,
                            change.Kind,
                            change.Path
                        });
                    }
                }

                foreach (var folderId in affectedFolders)
                {
                    var state = await _repository.GetExternalFolderIndexStateAsync(
                        folderId,
                        token).ConfigureAwait(false);
                    RaiseChanged(folderId, state, contentChanged: true);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task RunValidationPumpAsync()
    {
        var token = _lifetime.Token;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), _timeProvider);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                ExternalFolderSetting[] folders;
                lock (_gate) folders = _folders.Values.Select(Snapshot).ToArray();
                var now = _timeProvider.GetUtcNow();
                foreach (var folder in folders)
                {
                    var state = await _repository.GetExternalFolderIndexStateAsync(
                        folder.Id,
                        token).ConfigureAwait(false);
                    if (state?.NextValidationAt is null || state.NextValidationAt <= now)
                    {
                        _ = EnsureFolderIndexedInBackgroundAsync(folder, forceValidation: true);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureFolderIndexedInBackgroundAsync(
        ExternalFolderSetting folder,
        bool forceValidation = false)
    {
        try
        {
            await EnsureFolderIndexedAsync(
                folder,
                forceValidation,
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warning("external-folder-index", "Background external folder indexing failed.", ex, new
            {
                folder.Id,
                folder.Path
            });
        }
    }

    private async Task RemoveCompletedScanAsync(
        string folderId,
        Task<ExternalFolderIndexState?> scanTask)
    {
        try
        {
            await scanTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                if (_scanTasks.TryGetValue(folderId, out var current)
                    && ReferenceEquals(current, scanTask))
                {
                    _scanTasks.Remove(folderId);
                }
            }
        }
    }

    private void RaiseChanged(
        string folderId,
        ExternalFolderIndexState? state,
        bool contentChanged) =>
        IndexChanged?.Invoke(
            this,
            new ExternalFolderIndexChangedEventArgs(folderId, state, contentChanged));

    private static bool IsSupportedImage(string path) =>
        SupportedImageExtensions.Contains(Path.GetExtension(path));

    internal static string NormalizePath(string path) =>
        Path.GetFullPath(path).ToUpperInvariant();

    private static string ChangeKey(ExternalFileChange change) =>
        $"{change.FolderId}\0{NormalizePath(change.Path)}";

    private static ExternalFolderSetting Snapshot(ExternalFolderSetting folder) => new()
    {
        Id = folder.Id,
        Name = folder.Name,
        Path = Path.GetFullPath(folder.Path),
        AddedAt = folder.AddedAt
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    internal enum ExternalFileChangeKind
    {
        Changed,
        Deleted,
        Renamed
    }

    private sealed record ExternalFileChange(
        string FolderId,
        ExternalFileChangeKind Kind,
        string Path,
        string? OldPath);
}
