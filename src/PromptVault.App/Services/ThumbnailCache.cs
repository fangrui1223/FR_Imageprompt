using System.Windows.Media.Imaging;

namespace PromptVault.App.Services;

public static class ThumbnailCache
{
    private const int Capacity = 320;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (BitmapSource Image, LinkedListNode<string> Node)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Lru = new();

    public static Task<BitmapSource> LoadAsync(string path) => Task.Run(() =>
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var hit))
            {
                Lru.Remove(hit.Node); Lru.AddFirst(hit.Node); return hit.Image;
            }
        }

        var image = ImagePipeline.LoadPreview(path, 520);
        lock (Gate)
        {
            var node = Lru.AddFirst(path);
            Cache[path] = (image, node);
            while (Cache.Count > Capacity && Lru.Last is { } last)
            {
                Cache.Remove(last.Value); Lru.RemoveLast();
            }
        }
        return image;
    });
}
