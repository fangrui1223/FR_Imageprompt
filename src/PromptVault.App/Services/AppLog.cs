using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptVault.App.Services;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = ResolveDirectoryPath();
    private static int _retentionApplied;

    public static string CurrentLogPath =>
        Path.Combine(DirectoryPath, $"app-{DateTimeOffset.Now:yyyyMMdd}.jsonl");

    internal static string LogDirectoryPath => DirectoryPath;

    public static void Information(string area, string message, object? data = null) =>
        Write("information", area, message, null, data);

    public static void Warning(string area, string message, Exception? exception = null, object? data = null) =>
        Write("warning", area, message, exception, data);

    public static void Error(string area, Exception exception, string? message = null, object? data = null) =>
        Write("error", area, message ?? exception.Message, exception, data);

    private static void Write(
        string level,
        string area,
        string message,
        Exception? exception,
        object? data)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            ApplyRetentionOnce();
            var entry = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level,
                area,
                message = Limit(DiagnosticRedactor.RedactText(message)),
                exceptionType = exception?.GetType().FullName,
                exceptionMessage = exception is null
                    ? null
                    : Limit(DiagnosticRedactor.RedactText(exception.Message)),
                stackTrace = exception is null
                    ? null
                    : Limit(DiagnosticRedactor.RedactText(exception.StackTrace), 16000),
                data = DiagnosticRedactor.RedactObject(data)
            };
            var json = JsonSerializer.Serialize(entry);
            lock (Gate)
            {
                File.AppendAllText(CurrentLogPath, json + Environment.NewLine);
            }
        }
        catch
        {
            // Logging cannot be allowed to mask the original operation or crash.
        }
    }

    private static string? Limit(string? value, int length = 4000) =>
        value is null || value.Length <= length ? value : value[..length];

    private static void ApplyRetentionOnce()
    {
        if (Interlocked.Exchange(ref _retentionApplied, 1) != 0) return;
        var threshold = DateTime.UtcNow.AddDays(-14);
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "app-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < threshold) File.Delete(path);
            }
            catch
            {
                // Retention is best effort; logging must remain available.
            }
        }
    }

    private static string ResolveDirectoryPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("PROMPTVAULT_LOG_DIRECTORY");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptVault",
                "logs")
            : Path.GetFullPath(overridePath);
    }
}

internal static partial class DiagnosticRedactor
{
    private static readonly string[] SensitiveNames =
    [
        "prompt", "apikey", "api_key", "authorization", "bearer", "token", "secret",
        "password", "credential", "image", "originalpath", "sourcepath", "payload"
    ];

    private static readonly string[] PathNames =
    [
        "path", "directory", "root", "folder", "backup"
    ];

    public static JsonElement? RedactObject(object? value)
    {
        if (value is null) return null;
        using var source = JsonDocument.Parse(JsonSerializer.Serialize(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(writer, source.RootElement, null);
        }
        using var result = JsonDocument.Parse(stream.ToArray());
        return result.RootElement.Clone();
    }

    public static string RedactJsonLine(string line)
    {
        try
        {
            using var source = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteElement(writer, source.RootElement, null);
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return RedactText(line) ?? "";
        }
    }

    public static string? RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = CredentialPattern().Replace(value, "$1[REDACTED]");
        result = BearerPattern().Replace(result, "$1[REDACTED]");
        result = WindowsPathPattern().Replace(result, match =>
        {
            var fileName = Path.GetFileName(match.Value.TrimEnd('\\'));
            return string.IsNullOrWhiteSpace(fileName)
                ? "[REDACTED_PATH]"
                : $"[REDACTED_PATH]\\{fileName}";
        });
        return result;
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName is not null && IsSensitive(propertyName))
        {
            writer.WriteStringValue("[REDACTED]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsPath(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue(RedactPath(property.Value.GetString()));
                    }
                    else
                    {
                        WriteElement(writer, property.Value, property.Name);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray()) WriteElement(writer, child, propertyName);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(element.GetString()));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name)
    {
        var normalized = name.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return SensitiveNames.Any(candidate =>
            normalized.Contains(
                candidate.Replace("_", "", StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPath(string name) =>
        PathNames.Any(candidate => name.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string RedactPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var fileName = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(fileName) ? "[REDACTED_PATH]" : $"[REDACTED_PATH]\\{fileName}";
    }

    [GeneratedRegex(@"(?i)((?:api[-_ ]?key|token|secret|password)\s*[:=]\s*)[^\s,;""']+")]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9._~+/\-=]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\b[A-Za-z]:\\(?:[^<>:""/\\|?*\r\n]+\\)*[^<>:""/\\|?*\r\n]*")]
    private static partial Regex WindowsPathPattern();
}
