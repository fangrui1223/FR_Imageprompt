using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PromptVault.Core;

namespace PromptVault.App.Services;

public sealed class CaptureCoordinator
{
    public static readonly TimeSpan PromptWaitWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly LibraryRepository _repository;
    private readonly HttpClient _http;

    public CaptureCoordinator(LibraryRepository repository, HttpClient? httpClient = null)
    {
        _repository = repository;
        _http = httpClient ?? Http;
    }

    public async Task<PendingCapture> CreateFromFileAsync(string sourcePath, CancellationToken cancellationToken = default)
        => await CreateFromFileAsync(sourcePath, null, cancellationToken).ConfigureAwait(false);

    public async Task<PendingCapture> CreateFromFileAsync(
        string sourcePath,
        uint? clipboardSequence,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension)) throw new NotSupportedException("暂不支持这种图片格式。");
        var staged = NewStagingPath(extension.ToLowerInvariant());
        try
        {
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 128,
                true);
            await using var output = new FileStream(
                staged,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteStagingFile(staged);
            throw;
        }

        return await RegisterAndProcessAsync(
            staged,
            extension.TrimStart('.').ToLowerInvariant(),
            clipboardSequence,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PendingCapture> CreateFromUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new NotSupportedException("只支持 http/https 图片链接。");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var extension = ExtensionFromUriOrContentType(uri, response.Content.Headers.ContentType?.MediaType);
        if (!SupportedExtensions.Contains(extension)) throw new NotSupportedException("拖入的链接不是可收录的图片。");

        var staged = NewStagingPath(extension.ToLowerInvariant());
        var handedToStateMachine = false;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            handedToStateMachine = true;
            return await RegisterAndProcessAsync(
                staged,
                extension.TrimStart('.').ToLowerInvariant(),
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!handedToStateMachine) TryDeleteStagingFile(staged);
            throw;
        }
    }

    public Task<PendingCapture> CreateFromBitmapAsync(BitmapSource bitmap, CancellationToken cancellationToken = default)
        => CreateFromBitmapAsync(bitmap, null, cancellationToken);

    public Task<PendingCapture> CreateFromBitmapAsync(
        BitmapSource bitmap,
        uint? clipboardSequence,
        CancellationToken cancellationToken = default)
    {
        var staged = NewStagingPath(".png");
        try
        {
            ImagePipeline.SaveClipboardPng(bitmap, staged);
        }
        catch
        {
            TryDeleteStagingFile(staged);
            throw;
        }
        return RegisterAndProcessAsync(staged, "png", clipboardSequence, cancellationToken);
    }

    public async Task<CaptureSaveResult> SaveAsync(
        PendingCapture pending,
        string prompt,
        string notes,
        long? categoryId,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) && categoryId is null)
        {
            throw new ArgumentException("没有提示词时必须先选择人工主分类。", nameof(prompt));
        }
        var bucket = pending.Hash[..2];
        // A newly created asset must not reuse paths awaiting post-commit deletion
        // by an older capture. Retries of this session still use the same paths.
        var filename = $"{pending.Hash}-{pending.SessionId:N}";
        var asset = new AssetInput(
            pending.Hash,
            _repository.Paths.ToRelative(Path.Combine(_repository.Paths.Originals, bucket, $"{filename}.{pending.Extension}")),
            _repository.Paths.ToRelative(Path.Combine(_repository.Paths.SmallThumbnails, bucket, $"{filename}.jpg")),
            _repository.Paths.ToRelative(Path.Combine(_repository.Paths.MediumThumbnails, bucket, $"{filename}.jpg")),
            pending.Width, pending.Height, pending.Format);
        var result = await _repository.SaveCaptureAsync(
            pending.SessionId,
            new SaveItemInput(asset, prompt, notes, categoryId, ParseTags(tags)),
            DateTimeOffset.UtcNow + UndoWindow,
            cancellationToken,
            persistStagedFiles: true).ConfigureAwait(false);
        pending.Dispose();
        return result;
    }

    public async Task DiscardAsync(
        CaptureSessionRecord session,
        PendingCapture? pending = null,
        CancellationToken cancellationToken = default)
    {
        if (session.State != CaptureState.Undone)
        {
            await _repository.TransitionCaptureAsync(
                session.Id,
                CaptureState.Undone,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (pending is not null)
        {
            pending.Dispose();
            return;
        }

        foreach (var relativePath in new[]
                 {
                     session.StagedOriginalPath,
                     session.StagedSmallPath,
                     session.StagedMediumPath
                 }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                TryDeleteStagingFile(_repository.Paths.ToAbsolute(relativePath!));
            }
            catch (Exception ex)
            {
                AppLog.Warning(
                    "capture-staging-cleanup",
                    "A discarded capture staging path could not be resolved.",
                    ex);
            }
        }
    }

    public async Task<PendingCapture?> RecoverAsync(
        CaptureSessionRecord session,
        CancellationToken cancellationToken = default)
    {
        if (!CaptureStateMachine.IsRecoverable(session.State)) return null;
        if (session.State == CaptureState.Failed)
        {
            return CanRehydratePrepared(session)
                ? await RehydratePreparedAsync(session, cancellationToken).ConfigureAwait(false)
                : null;
        }
        if (CanRehydratePrepared(session))
        {
            return await RehydratePreparedAsync(session, cancellationToken).ConfigureAwait(false);
        }

        var staged = _repository.Paths.ToAbsolute(session.StagedOriginalPath);
        if (!File.Exists(staged))
        {
            await MarkFailedAsync(session.Id, "待恢复图片文件已丢失。", cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (session.State == CaptureState.ImageDetected)
        {
            await _repository.TransitionCaptureAsync(
                session.Id,
                CaptureState.PreparingImage,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return await ProcessAsync(
            session.Id,
            session.CapturedAt,
            staged,
            session.Extension,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PendingCapture> RegisterAndProcessAsync(
        string staged,
        string format,
        uint? clipboardSequence,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var captureId = Guid.NewGuid();
        var registered = false;
        try
        {
            await _repository.CreateCaptureSessionAsync(
                new CaptureSessionInput(
                    captureId,
                    _repository.Paths.ToRelative(staged),
                    format,
                    now,
                    now + PromptWaitWindow,
                    clipboardSequence),
                cancellationToken).ConfigureAwait(false);
            registered = true;
            await _repository.TransitionCaptureAsync(
                captureId,
                CaptureState.PreparingImage,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return await ProcessAsync(captureId, now, staged, format, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!registered)
            {
                TryDeleteStagingFile(staged);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (registered)
            {
                var message = FriendlyPreparationError(ex);
                await MarkFailedAsync(captureId, message, CancellationToken.None).ConfigureAwait(false);
                throw new CapturePreparationException(captureId, message, ex);
            }
            TryDeleteStagingFile(staged);
            throw;
        }
    }

    private async Task<PendingCapture> ProcessAsync(
        Guid captureId,
        DateTimeOffset capturedAt,
        string staged,
        string format,
        CancellationToken cancellationToken)
    {
        var small = NewStagingPath(".small.jpg");
        var medium = NewStagingPath(".medium.jpg");
        try
        {
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                BitmapSource frame;
                try
                {
                    frame = ImagePipeline.DecodeFirstFrame(staged);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidDataException("图片解码失败。", ex);
                }
                var hash = ImagePipeline.PixelHash(frame);
                ImagePipeline.SaveJpegThumbnail(frame, small, 480, 82);
                ImagePipeline.SaveJpegThumbnail(frame, medium, 1600, 90);
                var existing = await _repository.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);
                var preview = ImagePipeline.LoadPreview(medium);
                await _repository.MarkCapturePreparedAsync(
                    captureId,
                    new PreparedCaptureInput(
                        _repository.Paths.ToRelative(small),
                        _repository.Paths.ToRelative(medium),
                        hash,
                        format,
                        frame.PixelWidth,
                        frame.PixelHeight),
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                return new PendingCapture
                {
                    SessionId = captureId,
                    StagedOriginal = staged,
                    StagedSmall = small,
                    StagedMedium = medium,
                    Hash = hash,
                    Extension = format == "jpeg" ? "jpg" : format,
                    Format = format,
                    Width = frame.PixelWidth,
                    Height = frame.PixelHeight,
                    Preview = preview,
                    ExistingItem = existing,
                    CapturedAt = capturedAt
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDeleteStagingFile(small);
            TryDeleteStagingFile(medium);
            await MarkFailedAsync(captureId, FriendlyPreparationError(ex), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PendingCapture> RehydratePreparedAsync(
        CaptureSessionRecord session,
        CancellationToken cancellationToken)
    {
        var original = _repository.Paths.ToAbsolute(session.StagedOriginalPath);
        var small = _repository.Paths.ToAbsolute(session.StagedSmallPath!);
        var medium = _repository.Paths.ToAbsolute(session.StagedMediumPath!);
        if (!File.Exists(original) || !File.Exists(small) || !File.Exists(medium))
        {
            throw new FileNotFoundException("捕获会话的暂存文件不完整。");
        }
        var existing = await _repository.FindByHashAsync(session.Hash!, cancellationToken).ConfigureAwait(false);
        return new PendingCapture
        {
            SessionId = session.Id,
            StagedOriginal = original,
            StagedSmall = small,
            StagedMedium = medium,
            Hash = session.Hash!,
            Extension = session.Extension,
            Format = session.Format!,
            Width = session.Width,
            Height = session.Height,
            Preview = ImagePipeline.LoadPreview(medium),
            ExistingItem = existing,
            CapturedAt = session.CapturedAt
        };
    }

    private string NewStagingPath(string extension)
    {
        Directory.CreateDirectory(_repository.Paths.Staging);
        return Path.Combine(_repository.Paths.Staging, $"{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warning("capture-staging-cleanup", "A capture staging file could not be removed.", ex);
        }
    }

    private static string ExtensionFromUriOrContentType(Uri uri, string? contentType)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (SupportedExtensions.Contains(extension)) return extension;
        return contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            _ => extension
        };
    }

    private static bool CanRehydratePrepared(CaptureSessionRecord session) =>
        !string.IsNullOrWhiteSpace(session.Hash)
        && !string.IsNullOrWhiteSpace(session.Format)
        && !string.IsNullOrWhiteSpace(session.StagedSmallPath)
        && !string.IsNullOrWhiteSpace(session.StagedMediumPath)
        && session.Width > 0
        && session.Height > 0;

    private async Task MarkFailedAsync(
        Guid captureId,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await _repository.GetCaptureSessionAsync(captureId, cancellationToken).ConfigureAwait(false);
            if (session is not null && session.State != CaptureState.Failed)
            {
                await _repository.TransitionCaptureAsync(
                    captureId,
                    CaptureState.Failed,
                    error,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception transitionError)
        {
            AppLog.Warning(
                "capture-state",
                "A failed capture could not persist its error state.",
                transitionError);
        }
    }

    private static string FriendlyPreparationError(Exception ex) =>
        ex is FileNotFoundException
            ? "图片文件已不存在。"
            : ex is UnauthorizedAccessException
                ? "没有权限读取这张图片。"
                : ex is InvalidDataException
                    or FileFormatException
                    or NotSupportedException
                    or COMException
                    or ArgumentException
                    ? "图片已损坏或格式暂不支持。"
                    : $"图片准备失败：{ex.Message}";

    private static IReadOnlyList<string> ParseTags(IEnumerable<string> values) => values
        .SelectMany(x => x.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
}

public sealed class CapturePreparationException : Exception
{
    public CapturePreparationException(Guid captureId, string message, Exception innerException)
        : base(message, innerException) =>
        CaptureId = captureId;

    public Guid CaptureId { get; }
}
