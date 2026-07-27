using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptVault.App.Services;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string LibraryRoot { get; set; } = "";
    public bool OldestFirst { get; set; }
    public bool CaptureListeningEnabled { get; set; } = true;
    public bool ReducedMotionEnabled { get; set; }
    public bool InspectorPinned { get; set; }
    public string EdgeMenuSensitivity { get; set; } = EdgeIntentProfile.NormalSensitivity;
    public bool EdgeMenusAlwaysVisible { get; set; }
    public bool OnlineAiEnabled { get; set; }
    public string OnlineAiEndpoint { get; set; } = "";
    public string OnlineAiModel { get; set; } = "";
    public bool OnlineAiIncludeExistingPrompt { get; set; }
    public int ModelBackupRetentionCount { get; set; } = 2;
    public List<ExternalFolderSetting> ExternalFolders { get; set; } = [];
    public Dictionary<string, GalleryLayoutPreference> GalleryLayouts { get; set; } = [];
    [JsonIgnore] public string? RecoveryNotice { get; private set; }
    [JsonIgnore] public string? RecoveryBackupPath { get; private set; }
    [JsonIgnore] public string StorageFilePath => StoragePath ?? SettingsPath;
    [JsonIgnore] private string? StoragePath { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptVault", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        if (!File.Exists(path)) return new AppSettings { StoragePath = path };
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("设置文件内容为空。");
            settings.ExternalFolders ??= [];
            settings.GalleryLayouts ??= [];
            settings.EdgeMenuSensitivity = EdgeIntentProfile.NormalizeSensitivity(settings.EdgeMenuSensitivity);
            settings.ModelBackupRetentionCount = Math.Clamp(settings.ModelBackupRetentionCount, 0, 10);
            foreach (var preference in settings.GalleryLayouts.Values)
            {
                preference.Normalize();
            }
            settings.StoragePath = path;
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var backupPath = PreserveCorruptFile(path);
            return new AppSettings
            {
                StoragePath = path,
                RecoveryBackupPath = backupPath,
                RecoveryNotice = $"设置文件已损坏，FR_Imageprompt 已保留原文件并恢复默认设置。\n损坏文件：{backupPath}"
            };
        }
    }

    public GalleryLayoutPreference GetGalleryLayout(string sourceKey)
    {
        if (!GalleryLayouts.TryGetValue(sourceKey, out var preference))
        {
            preference = new GalleryLayoutPreference();
            GalleryLayouts[sourceKey] = preference;
        }
        preference.Normalize();
        return preference;
    }

    public void Save()
    {
        var path = StoragePath ?? SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, true);
    }

    private static string PreserveCorruptFile(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var backupPath = Path.Combine(
            directory,
            $"{name}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}{extension}");
        try
        {
            File.Move(path, backupPath);
            return backupPath;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"设置文件已损坏，但无法将原文件移到安全位置“{backupPath}”。请先复制该文件再重试。",
                ex);
        }
    }
}

public sealed class GalleryLayoutPreference
{
    public const string WaterfallMode = "Waterfall";
    public const string JustifiedMode = "Justified";
    public const string CompactDensity = "Compact";
    public const string ComfortableDensity = "Comfortable";
    public const string SpaciousDensity = "Spacious";
    public const string CustomDensity = "Custom";

    public string Mode { get; set; } = WaterfallMode;
    public string Density { get; set; } = ComfortableDensity;
    public double Spacing { get; set; } = GalleryLayoutEngine.DefaultSpacing;
    public double TargetSize { get; set; } = GalleryLayoutEngine.DefaultTargetSize;

    public GalleryLayoutOptions ToOptions()
    {
        Normalize();
        return new GalleryLayoutOptions(
            string.Equals(Mode, JustifiedMode, StringComparison.Ordinal)
                ? GalleryLayoutMode.Justified
                : GalleryLayoutMode.Waterfall,
            Spacing,
            TargetSize);
    }

    public void ApplyDensity(string density)
    {
        Density = density;
        (Spacing, TargetSize) = density switch
        {
            CompactDensity => (8, 240),
            SpaciousDensity => (22, 400),
            _ => (GalleryLayoutEngine.DefaultSpacing, GalleryLayoutEngine.DefaultTargetSize)
        };
        Normalize();
    }

    public void Normalize()
    {
        Mode = string.Equals(Mode, JustifiedMode, StringComparison.Ordinal)
            ? JustifiedMode
            : WaterfallMode;
        Density = Density is CompactDensity or ComfortableDensity or SpaciousDensity or CustomDensity
            ? Density
            : ComfortableDensity;
        Spacing = Math.Clamp(
            double.IsFinite(Spacing) ? Spacing : GalleryLayoutEngine.DefaultSpacing,
            GalleryLayoutEngine.MinimumSpacing,
            GalleryLayoutEngine.MaximumSpacing);
        TargetSize = Math.Clamp(
            double.IsFinite(TargetSize) ? TargetSize : GalleryLayoutEngine.DefaultTargetSize,
            GalleryLayoutEngine.MinimumTargetSize,
            GalleryLayoutEngine.MaximumTargetSize);
    }
}

public sealed class ExternalFolderSetting
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
