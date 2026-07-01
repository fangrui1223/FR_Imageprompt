using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.App;

public sealed record GalleryEntry(
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
    DateTimeOffset? DeletedAt,
    bool IsExternal,
    string? ExternalFolderId)
{
    public static GalleryEntry FromLibrary(GalleryItem item) => new(
        item.Id,
        item.Hash,
        item.OriginalPath,
        item.ThumbnailPath,
        item.Width,
        item.Height,
        item.Format,
        item.Prompt,
        item.Notes,
        item.CategoryId,
        item.CategoryName,
        item.Tags,
        item.CreatedAt,
        item.DeletedAt,
        false,
        null);

    public GalleryItem ToLibraryItem() => new(
        Id,
        Hash,
        OriginalPath,
        ThumbnailPath,
        Width,
        Height,
        Format,
        Prompt,
        Notes,
        CategoryId,
        CategoryName,
        Tags,
        CreatedAt,
        DeletedAt);
}

public sealed class GalleryCardViewModel : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _loading;
    private bool _isSelected;
    private double _layoutWidth;
    private double _imageHeight;

    public GalleryCardViewModel(GalleryEntry item, LibraryPaths paths, double layoutWidth, double imageHeight, bool isSelected = false)
    {
        Item = item;
        Paths = paths;
        _layoutWidth = layoutWidth;
        _imageHeight = imageHeight;
        _isSelected = isSelected;
    }

    public GalleryEntry Item { get; }
    public LibraryPaths Paths { get; }
    public long Id => Item.Id;
    public bool IsExternal => Item.IsExternal;
    public string CategoryName => Item.CategoryName;
    public string Tags => Item.Tags;
    public double AspectRatio => Item.Height <= 0 ? 1 : Item.Width / (double)Item.Height;
    public double LayoutWidth { get => _layoutWidth; private set { if (Math.Abs(_layoutWidth - value) < 0.1) return; _layoutWidth = value; OnPropertyChanged(); } }
    public double ImageHeight { get => _imageHeight; private set { if (Math.Abs(_imageHeight - value) < 0.1) return; _imageHeight = value; OnPropertyChanged(); OnPropertyChanged(nameof(CardHeight)); } }
    public double CardHeight => ImageHeight + 42;
    public BitmapSource? Thumbnail { get => _thumbnail; private set { _thumbnail = value; OnPropertyChanged(); } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
    public string OriginalPath => Item.IsExternal ? Item.OriginalPath : Paths.ToAbsolute(Item.OriginalPath);
    public string ThumbnailPath => Item.IsExternal ? Item.ThumbnailPath : Paths.ToAbsolute(Item.ThumbnailPath);

    public void UpdateLayout(double layoutWidth, double imageHeight)
    {
        LayoutWidth = layoutWidth;
        ImageHeight = imageHeight;
    }

    public async Task LoadAsync()
    {
        if (_thumbnail is not null || _loading) return;
        _loading = true;
        try { Thumbnail = await ThumbnailCache.LoadAsync(ThumbnailPath); }
        finally { _loading = false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class GalleryRow
{
    public ObservableCollection<GalleryCardViewModel> Items { get; } = new();
}
