namespace PromptVault.Core;

public sealed record CategoryRecord(long Id, string Name, string AiDescription, int SortOrder);

public sealed record GalleryItem(
    long Id,
    string Hash,
    string OriginalPath,
    string ThumbnailPath,
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

public sealed record SearchOptions(
    string Query = "",
    long? CategoryId = null,
    string Tag = "",
    bool IncludeTrash = false,
    bool OldestFirst = false,
    int Limit = 500,
    int Offset = 0);
