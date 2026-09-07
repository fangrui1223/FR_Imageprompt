using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptVault.Core;

public enum BoardNoteTextAlignment
{
    Left,
    Center,
    Right
}

public sealed record BoardNoteStyle(
    double FontSize,
    double LineSpacing,
    BoardNoteTextAlignment Alignment,
    string TextColor,
    bool BackgroundEnabled,
    string BackgroundColor,
    double BackgroundOpacity,
    double CornerRadius,
    double VerticalPadding)
{
    public static BoardNoteStyle Default { get; } = new(
        28,
        1.2,
        BoardNoteTextAlignment.Left,
        "#F4F7FB",
        true,
        "#253445",
        0.92,
        12,
        12);

    public BoardNoteStyle Normalize() => new(
        Math.Clamp(double.IsFinite(FontSize) ? FontSize : Default.FontSize, 8, 300),
        Math.Clamp(double.IsFinite(LineSpacing) ? LineSpacing : Default.LineSpacing, 1, 10),
        Enum.IsDefined(Alignment) ? Alignment : Default.Alignment,
        NormalizeColor(TextColor, Default.TextColor),
        BackgroundEnabled,
        NormalizeColor(BackgroundColor, Default.BackgroundColor),
        Math.Clamp(
            double.IsFinite(BackgroundOpacity) ? BackgroundOpacity : Default.BackgroundOpacity,
            0,
            1),
        Math.Clamp(double.IsFinite(CornerRadius) ? CornerRadius : Default.CornerRadius, 0, 200),
        Math.Clamp(double.IsFinite(VerticalPadding) ? VerticalPadding : Default.VerticalPadding, 0, 200));

    public static BoardNoteStyle FromLegacy(string? style) =>
        (style ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "rose" => Legacy("#231F18", "#F7C4CD"),
            "blue" => Legacy("#231F18", "#B7DAEB"),
            "slate" => Legacy("#231F18", "#C8CFD5"),
            _ => Legacy("#231F18", "#F5E099")
        };

    private static BoardNoteStyle Legacy(string textColor, string backgroundColor) => new(
        15,
        1.2,
        BoardNoteTextAlignment.Left,
        textColor,
        true,
        backgroundColor,
        1,
        7,
        10);

    private static string NormalizeColor(string? value, string fallback)
    {
        value = value?.Trim();
        return value is not null && Regex.IsMatch(
            value,
            "^#[0-9A-Fa-f]{6}$",
            RegexOptions.CultureInvariant)
            ? value.ToUpperInvariant()
            : fallback;
    }
}

public static class BoardNoteStyleCodec
{
    public const string Prefix = "m10:";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static BoardNoteStyle Decode(string? storage)
    {
        if (string.IsNullOrWhiteSpace(storage)) return BoardNoteStyle.FromLegacy("yellow");
        var normalized = storage.Trim();
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
            return BoardNoteStyle.FromLegacy(normalized);
        try
        {
            var encoded = normalized[Prefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return (JsonSerializer.Deserialize<BoardNoteStyle>(json, JsonOptions)
                    ?? BoardNoteStyle.Default)
                .Normalize();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException)
        {
            return BoardNoteStyle.Default;
        }
    }

    public static string Encode(BoardNoteStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var json = JsonSerializer.Serialize(style.Normalize(), JsonOptions);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string NormalizeStorage(string? storage)
    {
        var value = storage?.Trim().ToLowerInvariant();
        if (value is "yellow" or "rose" or "blue" or "slate") return value;
        return Encode(Decode(storage));
    }
}

public static class BoardNotePresetDefaults
{
    public const int SlotCount = 5;

    public static IReadOnlyList<BoardNoteStyle> Create() =>
    [
        BoardNoteStyle.Default,
        BoardNoteStyle.Default with
        {
            FontSize = 64,
            Alignment = BoardNoteTextAlignment.Center,
            BackgroundEnabled = false,
            VerticalPadding = 4
        },
        BoardNoteStyle.Default with
        {
            FontSize = 36,
            TextColor = "#63D7F7",
            BackgroundEnabled = false,
            VerticalPadding = 4
        },
        BoardNoteStyle.Default with
        {
            FontSize = 32,
            TextColor = "#2A2118",
            BackgroundColor = "#FFB04C",
            BackgroundOpacity = 0.94,
            CornerRadius = 16
        },
        BoardNoteStyle.Default with
        {
            FontSize = 18,
            TextColor = "#E8EDF4",
            BackgroundColor = "#111720",
            BackgroundOpacity = 0.72,
            CornerRadius = 8,
            VerticalPadding = 8
        }
    ];

    public static List<BoardNoteStyle> Normalize(IReadOnlyList<BoardNoteStyle>? presets)
    {
        var defaults = Create();
        var result = new List<BoardNoteStyle>(SlotCount);
        for (var index = 0; index < SlotCount; index++)
        {
            var candidate = presets is not null && index < presets.Count
                ? presets[index]
                : null;
            result.Add(candidate?.Normalize() ?? defaults[index]);
        }
        return result;
    }
}
