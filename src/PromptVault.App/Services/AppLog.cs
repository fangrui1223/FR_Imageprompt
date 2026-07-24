using System.Text.Json;

namespace PromptVault.App.Services;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = ResolveDirectoryPath();

    public static string CurrentLogPath =>
        Path.Combine(DirectoryPath, $"app-{DateTimeOffset.Now:yyyyMMdd}.jsonl");

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
            var entry = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level,
                area,
                message = Limit(message),
                exceptionType = exception?.GetType().FullName,
                exceptionMessage = exception is null ? null : Limit(exception.Message),
                stackTrace = exception is null ? null : Limit(exception.StackTrace, 16000),
                data
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
