using System.Windows;
using PromptVault.Core;

namespace PromptVault.App;

public sealed record MainWindowSnapshot(
    double Left,
    double Top,
    double Width,
    double Height,
    WindowState WindowState,
    bool Topmost,
    string SearchText,
    string TagText,
    long? CategoryId,
    string? ExternalFolderId,
    bool ShowTrash,
    bool FavoritesOnly,
    bool OldestFirst,
    bool MultiSelectMode,
    long[] SelectedItemIds,
    long? ViewerItemId,
    bool InspectorVisible,
    double InspectorWidth,
    GallerySessionSnapshot? GallerySession);

public sealed record GallerySessionSnapshot(
    GalleryEntry[] Items,
    GalleryRow[] Rows,
    double LayoutWidth,
    long TotalCount,
    bool HasMoreItems,
    SearchOptions? ActiveSearch,
    GalleryPageCursor? NextPageCursor,
    ExternalFilePageCursor? NextExternalCursor,
    bool FastBrowseIndexing,
    double VerticalOffset);

public sealed record MainWindowHandoffMetrics(
    bool Succeeded,
    bool OldWindowVisibleUntilReady,
    bool ReplacementHiddenUntilReady,
    int PreparedRenderFrames,
    bool ReplacementVisibleAfterCommit,
    bool OldWindowClosedAfterCommit,
    string? Error);

public sealed record MainWindowHandoffValidation(
    bool Passed,
    bool TransparentWindowReady,
    bool RowsReused,
    bool ItemsRetained,
    bool SelectionRetained,
    double ExpectedVerticalOffset,
    double ActualVerticalOffset,
    bool OffsetRetained,
    double ExpectedLayoutWidth,
    double ActualLayoutWidth,
    bool LayoutRetained,
    int SessionRestores,
    int GalleryReflows,
    int PreparedRenderFrames);
