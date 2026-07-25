namespace PromptVault.Core;

public sealed class LibraryPaths
{
    public LibraryPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string Originals => Path.Combine(Root, "originals");
    public string SmallThumbnails => Path.Combine(Root, "thumbnails", "small");
    public string MediumThumbnails => Path.Combine(Root, "thumbnails", "medium");
    public string Models => Path.Combine(Root, "models");
    public string Staging => Path.Combine(Root, ".staging");
    public string DatabaseBackups => Path.Combine(Root, "backups", "database");
    public string Database => Path.Combine(Root, "promptvault.db");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Originals);
        Directory.CreateDirectory(SmallThumbnails);
        Directory.CreateDirectory(MediumThumbnails);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Staging);
    }

    public string ToRelative(string absolutePath) => Path.GetRelativePath(Root, absolutePath);

    public string ToAbsolute(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(Root, relativePath));
        var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("图库记录包含无效路径。");
        }

        return fullPath;
    }
}
