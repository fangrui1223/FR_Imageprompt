using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PromptVault.Core;

namespace PromptVault.App;

public sealed record SimilarityResultViewModel(
    GalleryItem Item,
    string Title,
    string Prompt,
    string ScoreText,
    BitmapSource? Preview);

public partial class SimilarityResultsWindow : Window
{
    public SimilarityResultsWindow(
        Window owner,
        LibraryRepository repository,
        IReadOnlyList<(GalleryItem Item, float Score)> results)
    {
        InitializeComponent();
        Owner = owner;
        SummaryText.Text = $"{results.Count} 个结果 · 双击打开检查器";
        ResultsList.ItemsSource = results.Select(result => new SimilarityResultViewModel(
            result.Item,
            Path.GetFileName(result.Item.OriginalPath),
            result.Item.Prompt,
            $"{result.Score:P1}",
            LoadPreview(repository.Paths.ToAbsolute(result.Item.ThumbnailPath)))).ToArray();
    }

    public event Action<GalleryItem>? ItemRequested;

    private void ResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is SimilarityResultViewModel selected)
            ItemRequested?.Invoke(selected.Item);
    }

    private static BitmapSource? LoadPreview(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 240;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
