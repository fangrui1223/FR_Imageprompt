using System.Windows;

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
    bool OldestFirst,
    bool MultiSelectMode,
    long[] SelectedItemIds,
    long? ViewerItemId);
