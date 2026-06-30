using System.Net.Http;
using System.Windows.Media.Imaging;
using PromptVault.Core;

namespace PromptVault.App.Services;

public sealed class CaptureCoordinator
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly LibraryRepository _repository;

    public CaptureCoordinator(LibraryRepository repository) => _repository = repository;

    public async Task<PendingCapture> CreateFromFileAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension)) throw new NotSupportedException("暂不支持这种图片格式。");
        var staged = NewStagingPath(extension.ToLowerInvariant());
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true))
        await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ProcessAsync(staged, extension.TrimStart('.').ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PendingCapture> CreateFromUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new NotSupportedException("只支持 http/https 图片链接。");
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var extension = ExtensionFromUriOrContentType(uri, response.Content.Headers.ContentType?.MediaType);
        if (!SupportedExtensions.Contains(extension)) throw new NotSupportedException("拖入的链接不是可收录的图片。");

        var staged = NewStagingPath(extension.ToLowerInvariant());
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await ProcessAsync(staged, extension.TrimStart('.').ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(staged); } catch { }
            throw;
        }
    }

    public Task<PendingCapture> CreateFromBitmapAsync(BitmapSource bitmap, CancellationToken cancellationToken = default)
    {
        var staged = NewStagingPath(".png");
        ImagePipeline.SaveClipboardPng(bitmap, staged);
        return ProcessAsync(staged, "png", cancellationToken);
    }

    public async Task<SaveResult> SaveAsync(PendingCapture pending, string prompt, string notes, long? categoryId, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("提示词不能为空。", nameof(prompt));
        AssetInput asset;
        var moved = new List<string>();

        try
        {
            if (pending.ExistingItem is { } existing)
            {
                asset = new AssetInput(existing.Hash, existing.OriginalPath, existing.ThumbnailPath, existing.ThumbnailPath,
                    existing.Width, existing.Height, existing.Format);
            }
            else
            {
                var bucket = pending.Hash[..2];
                var original = Path.Combine(_repository.Paths.Originals, bucket, $"{pending.Hash}.{pending.Extension}");
                var small = Path.Combine(_repository.Paths.SmallThumbnails, bucket, $"{pending.Hash}.jpg");
                var medium = Path.Combine(_repository.Paths.MediumThumbnails, bucket, $"{pending.Hash}.jpg");
                MoveAtomic(pending.StagedOriginal, original); moved.Add(original);
                MoveAtomic(pending.StagedSmall, small); moved.Add(small);
                MoveAtomic(pending.StagedMedium, medium); moved.Add(medium);
                asset = new AssetInput(pending.Hash, _repository.Paths.ToRelative(original), _repository.Paths.ToRelative(small),
                    _repository.Paths.ToRelative(medium), pending.Width, pending.Height, pending.Format);
            }

            var result = await _repository.SaveAsync(new SaveItemInput(asset, prompt, notes, categoryId, ParseTags(tags)), cancellationToken).ConfigureAwait(false);
            pending.Dispose();
            return result;
        }
        catch
        {
            if (pending.ExistingItem is null)
            {
                foreach (var path in moved) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
            }
            throw;
        }
    }

    private async Task<PendingCapture> ProcessAsync(string staged, string format, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = ImagePipeline.DecodeFirstFrame(staged);
                var hash = ImagePipeline.PixelHash(frame);
                var small = NewStagingPath(".small.jpg");
                var medium = NewStagingPath(".medium.jpg");
                ImagePipeline.SaveJpegThumbnail(frame, small, 480, 82);
                ImagePipeline.SaveJpegThumbnail(frame, medium, 1600, 90);
                var existing = await _repository.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);
                var preview = ImagePipeline.LoadPreview(medium);
                return new PendingCapture
                {
                    StagedOriginal = staged,
                    StagedSmall = small,
                    StagedMedium = medium,
                    Hash = hash,
                    Extension = format == "jpeg" ? "jpg" : format,
                    Format = format,
                    Width = frame.PixelWidth,
                    Height = frame.PixelHeight,
                    Preview = preview,
                    ExistingItem = existing
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(staged); } catch { }
            throw;
        }
    }

    private string NewStagingPath(string extension)
    {
        Directory.CreateDirectory(_repository.Paths.Staging);
        return Path.Combine(_repository.Paths.Staging, $"{Guid.NewGuid():N}{extension}");
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

    private static void MoveAtomic(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) { File.Delete(source); return; }
        File.Move(source, destination);
    }

    private static IReadOnlyList<string> ParseTags(IEnumerable<string> values) => values
        .SelectMany(x => x.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
}
