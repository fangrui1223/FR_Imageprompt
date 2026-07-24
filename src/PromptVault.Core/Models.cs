namespace PromptVault.Core;

public sealed record CategoryRecord(long Id, string Name, string AiDescription, int SortOrder);

public sealed record GalleryItem(
    long Id,
    string Hash,
    string OriginalPath,
    string ThumbnailPath,
    string MediumThumbnailPath,
    int Width,
    int Height,
    string Format,
    string Prompt,
    string Notes,
    long? CategoryId,
    string CategoryName,
    string Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt);

public sealed record AssetInput(
    string Hash,
    string OriginalPath,
    string ThumbnailPath,
    string MediumThumbnailPath,
    int Width,
    int Height,
    string Format);

public sealed record SaveItemInput(
    AssetInput Asset,
    string Prompt,
    string Notes,
    long? CategoryId,
    IReadOnlyList<string> Tags);

public sealed record SaveResult(long ItemId, bool WasDuplicate);

public sealed record FileDeletionFailure(string RelativePath, string Error);

public sealed record TrashPurgeResult(
    int DeletedAssets,
    int DeletedFiles,
    IReadOnlyList<FileDeletionFailure> FileDeletionFailures);

public sealed record OrphanedFileReport(
    IReadOnlyList<string> UnreferencedFiles,
    IReadOnlyList<string> MissingReferencedFiles,
    IReadOnlyList<long> AssetsWithoutItems);

public sealed record RepositoryDiagnostic(string Area, string Message, Exception? Exception = null);

public enum GalleryTrashScope
{
    Active,
    Trash
}

public enum GallerySourceKind
{
    Library,
    ExternalFolder
}

public enum GallerySortOrder
{
    NewestFirst,
    OldestFirst
}

public sealed record GalleryPageCursor(DateTimeOffset CreatedAt, long Id);

public sealed record GallerySearchPage(
    long TotalCount,
    IReadOnlyList<GalleryItem> Items,
    GalleryPageCursor? NextCursor);

public enum ExternalFolderIndexStatus
{
    Pending,
    Indexing,
    Ready,
    Missing,
    PermissionDenied,
    Failed
}

public enum ExternalFileAvailability
{
    Available,
    Missing,
    Unreadable,
    PermissionDenied
}

public enum ExternalThumbnailStatus
{
    Unknown,
    SourceReady,
    Failed
}

public sealed record ExternalFileFingerprint(
    long Id,
    string NormalizedPath,
    long FileSize,
    DateTimeOffset ModifiedAt,
    ExternalFileAvailability Availability);

public sealed record ExternalFileIndexInput(
    string Path,
    string NormalizedPath,
    string FileName,
    long FileSize,
    DateTimeOffset ModifiedAt,
    int Width,
    int Height,
    string Format,
    ExternalThumbnailStatus ThumbnailStatus,
    ExternalFileAvailability Availability,
    string? Error = null);

public sealed record ExternalFileIndexItem(
    long Id,
    string FolderId,
    string Path,
    string FileName,
    long FileSize,
    DateTimeOffset ModifiedAt,
    int Width,
    int Height,
    string Format,
    ExternalThumbnailStatus ThumbnailStatus);

public sealed record ExternalFilePageCursor(DateTimeOffset ModifiedAt, long Id);

public sealed record ExternalFileSearchPage(
    long TotalCount,
    IReadOnlyList<ExternalFileIndexItem> Items,
    ExternalFilePageCursor? NextCursor);

public sealed record ExternalFolderIndexState(
    string FolderId,
    string RootPath,
    ExternalFolderIndexStatus Status,
    long AvailableFiles,
    long MissingFiles,
    long FailedFiles,
    DateTimeOffset? LastScanStartedAt,
    DateTimeOffset? LastScanCompletedAt,
    DateTimeOffset? NextValidationAt,
    string? LastError,
    long ScanGeneration);

public sealed record SearchOptions(
    string Query = "",
    long? CategoryId = null,
    bool UncategorizedOnly = false,
    string Tag = "",
    GalleryTrashScope Trash = GalleryTrashScope.Active,
    GallerySourceKind Source = GallerySourceKind.Library,
    string? SourceId = null,
    GallerySortOrder Sort = GallerySortOrder.NewestFirst,
    int PageSize = 240,
    GalleryPageCursor? Cursor = null);
