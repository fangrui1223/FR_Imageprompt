using PromptVault.Core;
using System.Windows.Media.Imaging;

namespace PromptVault.App.Services;

public sealed class PendingCapture : IDisposable
{
    public required string StagedOriginal { get; init; }
    public required string StagedSmall { get; init; }
    public required string StagedMedium { get; init; }
    public required string Hash { get; init; }
    public required string Extension { get; init; }
    public required string Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required BitmapSource Preview { get; init; }
    public GalleryItem? ExistingItem { get; init; }
    public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        TryDelete(StagedOriginal);
        TryDelete(StagedSmall);
        TryDelete(StagedMedium);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
