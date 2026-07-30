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
    DateTimeOffset? DeletedAt,
    bool IsFavorite = false);

public sealed record GalleryBrowseItem(
    long Id,
    string Hash,
    string OriginalPath,
    string ThumbnailPath,
    string MediumThumbnailPath,
    int Width,
    int Height,
    string Format,
    long? CategoryId,
    string CategoryName,
    string Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt,
    bool IsFavorite = false);

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

public enum AiMetadataSource
{
    User,
    LocalModel,
    OnlineApi,
    Rule
}

public enum MetadataCandidateStatus
{
    Pending,
    Confirmed,
    Modified,
    Rejected
}

public sealed record MetadataCandidateInput(
    long ItemId,
    string FieldType,
    string Value,
    AiMetadataSource Source,
    string ProviderId,
    string ModelName,
    string ModelVersion,
    double? Confidence);

public sealed record MetadataCandidateRecord(
    long Id,
    long ItemId,
    string FieldType,
    string Value,
    AiMetadataSource Source,
    string ProviderId,
    string ModelName,
    string ModelVersion,
    double? Confidence,
    MetadataCandidateStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserMetadataRecord(
    long ItemId,
    string FieldType,
    string Value,
    long? SourceCandidateId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum AiJobStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed
}

public sealed record AiJobInput(
    long ItemId,
    string JobType,
    string ProviderId,
    string ModelVersion,
    string CacheKey,
    string Payload = "{}",
    int Priority = 0,
    int MaxAttempts = 3,
    DateTimeOffset? NotBefore = null);

public sealed record AiJobRecord(
    long Id,
    long ItemId,
    string JobType,
    string ProviderId,
    string ModelVersion,
    AiJobStatus Status,
    int Priority,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset NotBefore,
    string CacheKey,
    string Payload,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ImageEmbeddingRecord(
    long ItemId,
    string ProviderId,
    string ModelVersion,
    float[] Vector,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SimilarityMatch(long ItemId, float Score);

public sealed record BoardRecord(
    long Id,
    string Name,
    string BackgroundStyle,
    double ViewOffsetX,
    double ViewOffsetY,
    double Zoom,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BoardGroupRecord(
    long Id,
    long BoardId,
    string Name,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BoardItemPlacementInput(
    long CollectionItemId,
    double X,
    double Y,
    double Width,
    double Height,
    int ZIndex = 0,
    double Rotation = 0,
    double CropLeft = 0,
    double CropTop = 0,
    double CropRight = 0,
    double CropBottom = 0,
    long? GroupId = null);

public sealed record BoardItemRecord(
    long Id,
    long BoardId,
    long? AssetId,
    string SourcePathSnapshot,
    string? SourcePathOverride,
    string? OriginalPath,
    string? ThumbnailPath,
    string? MediumThumbnailPath,
    int NaturalWidth,
    int NaturalHeight,
    string Format,
    double X,
    double Y,
    double Width,
    double Height,
    int ZIndex,
    double Rotation,
    double CropLeft,
    double CropTop,
    double CropRight,
    double CropBottom,
    long? GroupId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BoardItemUpdate(
    long Id,
    double X,
    double Y,
    double Width,
    double Height,
    int ZIndex,
    double Rotation,
    double CropLeft,
    double CropTop,
    double CropRight,
    double CropBottom,
    long? GroupId,
    string? SourcePathOverride);

public sealed record BoardNoteRecord(
    long Id,
    long BoardId,
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    int ZIndex,
    string ColorStyle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BoardNoteUpdate(
    long Id,
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    int ZIndex,
    string ColorStyle);

public sealed record BoardDocument(
    BoardRecord Board,
    IReadOnlyList<BoardGroupRecord> Groups,
    IReadOnlyList<BoardItemRecord> Items,
    IReadOnlyList<BoardNoteRecord> Notes);

public readonly record struct BoardViewport(
    double OffsetX,
    double OffsetY,
    double Zoom,
    double Width,
    double Height);

public readonly record struct BoardWorldRect(
    double X,
    double Y,
    double Width,
    double Height);

public enum CaptureState
{
    ImageDetected = 0,
    PreparingImage = 1,
    WaitingForPrompt = 2,
    PromptDebouncing = 3,
    Saved = 4,
    WaitingForAi = 5,
    NeedsPrompt = 6,
    Failed = 7,
    Undone = 8
}

public sealed record CaptureSessionRecord(
    Guid Id,
    CaptureState State,
    string StagedOriginalPath,
    string? StagedSmallPath,
    string? StagedMediumPath,
    string? Hash,
    string Extension,
    string? Format,
    int Width,
    int Height,
    string Prompt,
    string Notes,
    long? CategoryId,
    string Tags,
    DateTimeOffset CapturedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset PromptDeadlineAt,
    long? SavedItemId,
    bool WasDuplicate,
    DateTimeOffset? UndoDeadlineAt,
    string? Error,
    uint? LastClipboardSequence,
    string? AiSummary);

public sealed record CaptureSessionInput(
    Guid Id,
    string StagedOriginalPath,
    string Extension,
    DateTimeOffset CapturedAt,
    DateTimeOffset PromptDeadlineAt,
    uint? ClipboardSequence = null);

public sealed record PreparedCaptureInput(
    string StagedSmallPath,
    string StagedMediumPath,
    string Hash,
    string Format,
    int Width,
    int Height);

public sealed record CaptureSaveResult(
    Guid CaptureId,
    long ItemId,
    bool WasDuplicate,
    DateTimeOffset UndoDeadlineAt);

public sealed record CaptureUndoResult(
    Guid CaptureId,
    long? ItemId,
    bool RestoredDuplicate,
    IReadOnlyList<string> DeletedRelativePaths,
    IReadOnlyList<FileDeletionFailure> FileDeletionFailures);

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

public sealed record GalleryBrowsePage(
    long TotalCount,
    IReadOnlyList<GalleryBrowseItem> Items,
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
    bool FavoritesOnly = false,
    GallerySortOrder Sort = GallerySortOrder.NewestFirst,
    int PageSize = 240,
    GalleryPageCursor? Cursor = null);
