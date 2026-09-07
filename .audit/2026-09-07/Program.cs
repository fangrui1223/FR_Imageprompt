using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using PromptVault.App.Services;
using PromptVault.Core;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static string RunRoot = "";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--initialize-m11-fixture"))
        {
            var root = Path.GetFullPath(args[0]);
            if (!root.Contains($"{Path.DirectorySeparatorChar}.m10-isolated{Path.DirectorySeparatorChar}m11-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Requires a new M11 synthetic fixture.");
            new LibraryRepository(new LibraryPaths(root)).InitializeAsync().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            return;
        }
        if (args.Contains("--board-history"))
        {
            BoardHistoryProbe.Run(args[0]);
            return;
        }
        RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string[] args)
    {
        var outputRoot = Path.GetFullPath(args[0]);
        RunRoot = Path.Combine(outputRoot, "synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RunRoot);
        await Case("upgrade-rollback-expired-files", UpgradeRollbackAsync);
        await Case("uri-capture-file-lock", UriCaptureAsync);
        await Case("url-model-install-file-lock", ModelDownloadAsync);
        await Case("stale-duplicate-after-permanent-delete", StaleDuplicateAsync);
        await Case("capture-undo-overwrites-newer-edit", UndoNewerEditAsync);
        await Case("settings-null-layout", NullLayoutAsync);
        SqliteConnection.ClearAllPools();
        var json = JsonSerializer.Serialize(new { runRoot = RunRoot, results = Results },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "audit-reproductions.json"), json);
        Console.WriteLine(json);
    }

    private static async Task Case(string name, Func<string, Task<object>> action)
    {
        try { Results.Add(new { name, observation = await action(name) }); }
        catch (Exception ex) { Results.Add(new { name, harnessError = ex.ToString() }); }
    }

    private static async Task<LibraryRepository> NewRepository(string name)
    {
        var repository = new LibraryRepository(new LibraryPaths(Path.Combine(RunRoot, name)));
        await repository.InitializeAsync();
        return repository;
    }

    private static async Task<object> UpgradeRollbackAsync(string name)
    {
        var repo = await NewRepository(name);
        var files = new[] { "originals/expired.png", "thumbnails/small/expired.jpg", "thumbnails/medium/expired.jpg" };
        foreach (var relative in files) await File.WriteAllBytesAsync(repo.Paths.ToAbsolute(relative), [1, 2, 3]);
        var saved = await repo.SaveAsync(new SaveItemInput(
            new AssetInput("audit-expired", files[0], files[1], files[2], 1, 1, "png"),
            "expired synthetic image", "", null, []));
        await repo.MoveToTrashAsync(saved.ItemId);
        // Version 12 adds only IF NOT EXISTS objects, so this creates a valid v11 migration fixture.
        await ExecuteSqlAsync(repo.Paths.Database, "UPDATE collection_items SET deleted_at='2000-01-01T00:00:00.0000000+00:00'; UPDATE schema_info SET version=11; PRAGMA user_version=11;");
        SqliteConnection.ClearAllPools();
        var reopened = new LibraryRepository(repo.Paths);
        string? error = null;
        try
        {
            await reopened.InitializeAsync(new LibraryUpgradeOptions(Checkpoint: (checkpoint, _) =>
            {
                if (checkpoint.Phase == LibraryUpgradePhase.ValidationCompleted)
                    throw new IOException("Synthetic failure after validation checkpoint");
                return ValueTask.CompletedTask;
            }));
        }
        catch (Exception ex) { error = ex.Message; }
        var restored = await reopened.FindByHashAsync("audit-expired");
        var missing = (await reopened.InspectOrphanedFilesAsync()).MissingReferencedFiles;
        return new { failureInjected = error is not null, databaseRowRestored = restored is not null,
            remainingFiles = files.Count(p => File.Exists(repo.Paths.ToAbsolute(p))), missingFiles = missing,
            recoveryReported = reopened.LastUpgradeRecovery?.Recovered,
            bugReproduced = restored is not null && missing.Count == 3 };
    }

    private static async Task<object> UriCaptureAsync(string name)
    {
        var repo = await NewRepository(name);
        var source = Path.Combine(repo.Paths.Root, "source.png");
        WritePng(source);
        var bytes = await File.ReadAllBytesAsync(source);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        });
        string? exceptionType = null;
        string? rootCause = null;
        int? hresult = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var pending = await new CaptureCoordinator(repo).CreateFromUriAsync(
                new Uri($"http://127.0.0.1:{address.Port}/source.png"), timeout.Token);
        }
        catch (Exception ex)
        {
            exceptionType = ex.GetType().Name;
            rootCause = ex.GetBaseException().Message;
            hresult = ex.GetBaseException().HResult;
        }
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        var inbox = await repo.GetCaptureInboxAsync();
        return new { exceptionType, rootCause, hresult, sourceStillDecodes = ImagePipeline.DecodeFirstFrame(source).PixelWidth == 32,
            failedSessions = inbox.Count(s => s.State == CaptureState.Failed),
            bugReproduced = exceptionType == nameof(CapturePreparationException) && hresult == unchecked((int)0x80070020) };
    }

    private static async Task<object> StaleDuplicateAsync(string name)
    {
        var repo = await NewRepository(name);
        var source = Path.Combine(repo.Paths.Root, "source.png");
        WritePng(source);
        var coordinator = new CaptureCoordinator(repo);
        var initial = await coordinator.CreateFromFileAsync(source);
        var saved = await coordinator.SaveAsync(initial, "first", "", null, []);
        var duplicate = await coordinator.CreateFromFileAsync(source);
        var cachedExisting = duplicate.ExistingItem is not null;
        await repo.MoveToTrashAsync(saved.ItemId);
        await repo.PermanentlyDeleteTrashItemsAsync([saved.ItemId]);
        var stagedBeforeSave = File.Exists(duplicate.StagedOriginal);
        var resaved = await coordinator.SaveAsync(duplicate, "new capture", "", null, []);
        var item = await repo.FindByHashAsync(duplicate.Hash);
        var missing = (await repo.InspectOrphanedFilesAsync()).MissingReferencedFiles;
        return new { cachedExisting, stagedBeforeSave, reportedSaveSuccess = resaved.ItemId > 0,
            reportedDuplicate = resaved.WasDuplicate, savedRowExists = item is not null,
            stagedAfterSave = File.Exists(duplicate.StagedOriginal), missingFiles = missing,
            bugReproduced = item is not null && missing.Count == 3 && !File.Exists(duplicate.StagedOriginal) };
    }

    private static async Task<object> ModelDownloadAsync(string name)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var filename in new[] { "clip/manifest.json", "clip/image_encoder.onnx" })
            {
                await using var entry = archive.CreateEntry(filename).Open();
                await entry.WriteAsync(Encoding.UTF8.GetBytes("synthetic model content"));
            }
        }
        var bytes = buffer.ToArray();
        using var http = new HttpClient(new SyntheticResponseHandler(bytes));
        var modelRoot = Path.Combine(RunRoot, name);
        try
        {
            await new ModelPackInstaller(http).InstallFromUrlAsync(
                new Uri("https://synthetic.invalid/model.zip"),
                Convert.ToHexString(SHA256.HashData(bytes)), modelRoot);
            return new { bugReproduced = false };
        }
        catch (Exception ex)
        {
            return new { bugReproduced = ex.HResult == unchecked((int)0x80070020),
                exceptionType = ex.GetType().Name, hresult = ex.HResult,
                modelInstalled = Directory.Exists(Path.Combine(modelRoot, "clip")), externalRequests = 0 };
        }
    }

    private sealed class SyntheticResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private static async Task<object> UndoNewerEditAsync(string name)
    {
        var repo = await NewRepository(name);
        var source = Path.Combine(repo.Paths.Root, "source.png");
        WritePng(source);
        var coordinator = new CaptureCoordinator(repo);
        var original = await coordinator.CreateFromFileAsync(source);
        var first = await coordinator.SaveAsync(original, "original prompt", "original notes", null, ["original"]);
        var duplicate = await coordinator.CreateFromFileAsync(source);
        await coordinator.SaveAsync(duplicate, "duplicate prompt", "duplicate notes", null, ["duplicate"]);
        await repo.UpdateItemDetailsAsync(first.ItemId, "newer manual prompt", "newer", "newer manual notes");
        var before = await repo.GetGalleryItemAsync(first.ItemId);
        string? conflict = null;
        try { await repo.UndoCaptureAsync(duplicate.SessionId, DateTimeOffset.UtcNow); }
        catch (InvalidOperationException ex) { conflict = ex.Message; }
        var after = await repo.GetGalleryItemAsync(first.ItemId);
        return new { beforePrompt = before?.Prompt, afterPrompt = after?.Prompt, beforeNotes = before?.Notes,
            afterNotes = after?.Notes, conflict, newerEditPreserved = conflict is not null && before == after,
            bugReproduced = before?.Prompt == "newer manual prompt" && after?.Prompt == "original prompt" };
    }

    private static async Task<object> NullLayoutAsync(string name)
    {
        var path = Path.Combine(RunRoot, name + ".json");
        await File.WriteAllTextAsync(path, """{"LibraryRoot":"synthetic","GalleryLayouts":{"library":null}}""");
        try
        {
            var settings = AppSettings.Load(path);
            return new { bugReproduced = false, recovered = settings.RecoveryNotice is not null };
        }
        catch (Exception ex) { return new { bugReproduced = ex is NullReferenceException, exceptionType = ex.GetType().Name }; }
    }

    private static async Task ExecuteSqlAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void WritePng(string path)
    {
        const int width = 32, height = 24;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i % 239);
            pixels[i + 1] = (byte)(37 + i % 173);
            pixels[i + 2] = 153;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
