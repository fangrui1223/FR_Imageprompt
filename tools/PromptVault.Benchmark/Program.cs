using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptVault.Core;

var options = BenchmarkOptions.Parse(args);
var machine = new MachineSnapshot(
    Environment.OSVersion.VersionString,
    Environment.ProcessorCount,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
    Environment.Version.ToString());
var run = new BenchmarkRun(
    DateTimeOffset.UtcNow,
    machine,
    options.Iterations,
    []);

foreach (var count in options.Counts)
{
    var root = options.FixedRoot is not null
        ? Path.GetFullPath(options.FixedRoot)
        : options.RetainRoot is null
        ? Path.Combine(Path.GetTempPath(), "PromptVaultBenchmark", $"{count}-{Guid.NewGuid():N}")
        : Path.Combine(Path.GetFullPath(options.RetainRoot), $"{count}-{Guid.NewGuid():N}");
    try
    {
        var paths = new LibraryPaths(root);
        var reuseFixedDatabase = options.FixedRoot is not null && File.Exists(paths.Database);
        _ = new LibraryRepository(paths);
        RetainedImageSet? retainedImages = null;
        var seed = Stopwatch.StartNew();
        if (!reuseFixedDatabase)
        {
            var bootstrap = new LibraryRepository(paths);
            await bootstrap.InitializeAsync();
            retainedImages = options.RetainRoot is null && options.FixedRoot is null
                ? null
                : CreateRetainedBenchmarkImages(
                    paths,
                    options.RetainedImageSource,
                    options.RetainedImageCount);
            await SeedAsync(paths.Database, count, retainedImages);
        }
        else
        {
            var existingCount = await CountItemsAsync(paths.Database);
            if (existingCount != count)
            {
                throw new InvalidDataException(
                    $"Fixed benchmark database contains {existingCount} items; expected {count}.");
            }
        }
        seed.Stop();
        SqliteConnection.ClearAllPools();

        var initialization = await MeasureAsync(
            "repository-initialize",
            Math.Min(5, options.Iterations),
            async () =>
            {
                SqliteConnection.ClearAllPools();
                var candidate = new LibraryRepository(paths);
                await candidate.InitializeAsync();
                return 0;
            });

        var repository = new LibraryRepository(paths);
        await repository.InitializeAsync();
        await repository.SearchPageAsync(new SearchOptions(PageSize: 50));
        GalleryPageCursor? deepCursor = null;
        var cursorHops = Math.Min(20, Math.Max(0, (count - 1) / 1000));
        for (var hop = 0; hop < cursorHops; hop++)
        {
            var page = await repository.SearchPageAsync(new SearchOptions(PageSize: 1000, Cursor: deepCursor));
            deepCursor = page.NextCursor;
            if (deepCursor is null) break;
        }

        var measurements = new List<ScenarioResult>
        {
            await MeasureAsync("gallery-first-page-1000", options.Iterations,
                () => repository.SearchPageAsync(new SearchOptions(PageSize: 1000))),
            await MeasureAsync("text-search-chinese-fragment", options.Iterations,
                () => repository.SearchPageAsync(new SearchOptions(Query: "赛博朋克", PageSize: 200))),
            await MeasureAsync("text-search-english-fragment", options.Iterations,
                () => repository.SearchPageAsync(new SearchOptions(Query: "ference comp", PageSize: 200))),
            await MeasureAsync("category-filter", options.Iterations,
                () => repository.SearchPageAsync(new SearchOptions(CategoryId: 1, PageSize: 200))),
            await MeasureAsync("tag-filter", options.Iterations,
                () => repository.SearchPageAsync(new SearchOptions(Tag: "夜景", PageSize: 200)))
        };
        if (deepCursor is not null)
        {
            measurements.Add(await MeasureAsync("gallery-keyset-page-1000", options.Iterations,
                () => repository.SearchPageAsync(new SearchOptions(PageSize: 1000, Cursor: deepCursor))));
        }

        run.Scales.Add(new ScaleResult(
            count,
            Math.Round(seed.Elapsed.TotalMilliseconds, 3),
            initialization,
            repository.FullTextSearchAvailable ? "fts5-trigram" : "like-fallback",
            new FileInfo(paths.Database).Length,
            options.RetainRoot is null && options.FixedRoot is null ? null : root,
            retainedImages?.Count ?? (options.FixedRoot is null ? null : options.RetainedImageCount),
            measurements));
        Console.WriteLine($"{count,6} items | init P95 {initialization.P95Ms,9:F2} ms | " +
                          string.Join(" | ", measurements.Select(x => $"{x.Name} P95 {x.P95Ms:F2} ms")));
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (options.RetainRoot is null && options.FixedRoot is null && Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch (IOException)
        {
            Console.Error.WriteLine($"Temporary benchmark data remains at: {root}");
        }
    }
}

var outputPath = Path.GetFullPath(options.OutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Report: {outputPath}");
if (options.Gate)
{
    var failures = run.Scales.SelectMany(scale =>
        new[] { scale.RepositoryInitialize }
            .Concat(scale.Scenarios)
            .Where(result =>
                result.P95Ms > (result.Name == "repository-initialize" ? 1500d : 80d))
            .Select(result =>
                $"{scale.ItemCount}:{result.Name} P95 {result.P95Ms:F3} ms exceeded "
                + $"{(result.Name == "repository-initialize" ? 1500 : 80)} ms"))
        .ToArray();
    foreach (var failure in failures) Console.Error.WriteLine($"GATE FAILED: {failure}");
    return failures.Length == 0 ? 0 : 2;
}
return 0;

static async Task<long> CountItemsAsync(string databasePath)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString());
    await connection.OpenAsync();
    var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM collection_items;";
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static RetainedImageSet CreateRetainedBenchmarkImages(
    LibraryPaths paths,
    string? sourcePath,
    int count)
{
    const string transparentPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    var bytes = sourcePath is null
        ? Convert.FromBase64String(transparentPng)
        : File.ReadAllBytes(Path.GetFullPath(sourcePath));
    var extension = sourcePath is null
        ? ".png"
        : Path.GetExtension(sourcePath).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(extension)) extension = ".img";

    for (var index = 0; index < count; index++)
    {
        var fileName = RetainedImageFileName(index, count, extension);
        var targets = new[]
        {
            Path.Combine(paths.Originals, fileName),
            Path.Combine(paths.SmallThumbnails, fileName),
            Path.Combine(paths.MediumThumbnails, fileName)
        };
        foreach (var target in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    return new RetainedImageSet(count, extension);
}

static string RetainedImageFileName(int index, int count, string extension) =>
    count == 1 ? $"benchmark{extension}" : $"benchmark-{index:D4}{extension}";

static async Task SeedAsync(string databasePath, int count, RetainedImageSet? retainedImages)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite
    }.ToString());
    await connection.OpenAsync();
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

    var tagIds = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["夜景"] = 1001,
        ["人物"] = 1002,
        ["产品"] = 1003,
        ["构图参考"] = 1004
    };
    foreach (var (tag, id) in tagIds)
    {
        var tagCommand = connection.CreateCommand();
        tagCommand.Transaction = transaction;
        tagCommand.CommandText = "INSERT INTO tags(id, name) VALUES($id, $name);";
        tagCommand.Parameters.AddWithValue("$id", id);
        tagCommand.Parameters.AddWithValue("$name", tag);
        await tagCommand.ExecuteNonQueryAsync();
    }

    var insertAsset = connection.CreateCommand();
    insertAsset.Transaction = transaction;
    insertAsset.CommandText = """
        INSERT INTO image_assets(id, hash, original_path, thumbnail_path, medium_thumbnail_path, width, height, format, created_at)
        VALUES($id, $hash, $original, $small, $medium, $width, $height, $format, $created);
        """;
    var assetId = insertAsset.Parameters.Add("$id", SqliteType.Integer);
    var hash = insertAsset.Parameters.Add("$hash", SqliteType.Text);
    var original = insertAsset.Parameters.Add("$original", SqliteType.Text);
    var small = insertAsset.Parameters.Add("$small", SqliteType.Text);
    var medium = insertAsset.Parameters.Add("$medium", SqliteType.Text);
    var format = insertAsset.Parameters.Add("$format", SqliteType.Text);
    var width = insertAsset.Parameters.Add("$width", SqliteType.Integer);
    var height = insertAsset.Parameters.Add("$height", SqliteType.Integer);
    var assetCreated = insertAsset.Parameters.Add("$created", SqliteType.Text);

    var insertItem = connection.CreateCommand();
    insertItem.Transaction = transaction;
    insertItem.CommandText = """
        INSERT INTO collection_items(id, asset_id, prompt, notes, category_id, created_at, updated_at)
        VALUES($id, $id, $prompt, $notes, $category, $created, $created);
        """;
    var itemId = insertItem.Parameters.Add("$id", SqliteType.Integer);
    var prompt = insertItem.Parameters.Add("$prompt", SqliteType.Text);
    var notes = insertItem.Parameters.Add("$notes", SqliteType.Text);
    var category = insertItem.Parameters.Add("$category", SqliteType.Integer);
    var itemCreated = insertItem.Parameters.Add("$created", SqliteType.Text);

    var insertItemTag = connection.CreateCommand();
    insertItemTag.Transaction = transaction;
    insertItemTag.CommandText = "INSERT INTO item_tags(item_id, tag_id, source) VALUES($item, $tag, 'user');";
    var taggedItem = insertItemTag.Parameters.Add("$item", SqliteType.Integer);
    var tagId = insertItemTag.Parameters.Add("$tag", SqliteType.Integer);

    var start = DateTimeOffset.UtcNow.AddDays(-count);
    for (var index = 1; index <= count; index++)
    {
        var created = start.AddMinutes(index).ToString("O");
        var retainedFileName = retainedImages is null
            ? null
            : RetainedImageFileName(
                (index - 1) % retainedImages.Count,
                retainedImages.Count,
                retainedImages.Extension);
        assetId.Value = index;
        hash.Value = $"benchmark-{index:D8}";
        original.Value = retainedFileName is not null
            ? $"originals/{retainedFileName}"
            : $"originals/{index % 256:x2}/benchmark-{index:D8}.jpg";
        small.Value = retainedFileName is not null
            ? $"thumbnails/small/{retainedFileName}"
            : $"thumbnails/small/{index % 256:x2}/benchmark-{index:D8}.jpg";
        medium.Value = retainedFileName is not null
            ? $"thumbnails/medium/{retainedFileName}"
            : $"thumbnails/medium/{index % 256:x2}/benchmark-{index:D8}.jpg";
        format.Value = retainedImages?.Extension.TrimStart('.') ?? "jpg";
        var (assetWidth, assetHeight) = (index % 8) switch
        {
            0 => (400, 1600),
            1 => (900, 1600),
            2 => (1000, 1500),
            3 => (1200, 1600),
            4 => (1200, 1200),
            5 => (1600, 1200),
            6 => (1600, 900),
            _ => (1600, 400)
        };
        width.Value = assetWidth;
        height.Value = assetHeight;
        assetCreated.Value = created;
        await insertAsset.ExecuteNonQueryAsync();

        itemId.Value = index;
        prompt.Value = index % 7 == 0
            ? $"赛博朋克 城市 夜景 霓虹灯 cinematic benchmark {index}"
            : $"visual reference composition lighting benchmark {index}";
        notes.Value = index % 11 == 0 ? "高对比度 光影 构图参考" : "";
        category.Value = (index % 6) + 1;
        itemCreated.Value = created;
        await insertItem.ExecuteNonQueryAsync();

        taggedItem.Value = index;
        tagId.Value = (index % 4) switch
        {
            0 => tagIds["夜景"],
            1 => tagIds["人物"],
            2 => tagIds["产品"],
            _ => tagIds["构图参考"]
        };
        await insertItemTag.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
}

static async Task<ScenarioResult> MeasureAsync<T>(string name, int iterations, Func<Task<T>> action)
{
    var samples = new double[iterations];
    var resultCount = 0;
    for (var index = 0; index < iterations; index++)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await action();
        stopwatch.Stop();
        samples[index] = stopwatch.Elapsed.TotalMilliseconds;
        resultCount = result switch
        {
            GallerySearchPage page => page.Items.Count,
            ICollection collection => collection.Count,
            IReadOnlyCollection<GalleryItem> items => items.Count,
            _ => 0
        };
    }

    Array.Sort(samples);
    return new ScenarioResult(
        name,
        resultCount,
        Math.Round(samples.Average(), 3),
        Math.Round(Percentile(samples, 0.50), 3),
        Math.Round(Percentile(samples, 0.95), 3),
        Math.Round(samples[^1], 3));
}

static double Percentile(double[] ordered, double percentile)
{
    var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
    return ordered[index];
}

internal sealed record MachineSnapshot(string Os, int LogicalProcessors, long AvailableMemoryBytes, string Dotnet);
internal sealed record ScenarioResult(string Name, int ResultCount, double MeanMs, double P50Ms, double P95Ms, double MaximumMs);
internal sealed record ScaleResult(
    int ItemCount,
    double SeedMs,
    ScenarioResult RepositoryInitialize,
    string SearchBackend,
    long DatabaseBytes,
    string? LibraryRoot,
    int? RetainedImageCount,
    List<ScenarioResult> Scenarios);
internal sealed record BenchmarkRun(DateTimeOffset StartedAtUtc, MachineSnapshot Machine, int Iterations, List<ScaleResult> Scales);
internal sealed record RetainedImageSet(int Count, string Extension);

internal sealed record BenchmarkOptions(
    int[] Counts,
    int Iterations,
    string OutputPath,
    string? RetainRoot,
    string? FixedRoot,
    string? RetainedImageSource,
    int RetainedImageCount,
    bool Gate)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var counts = new[] { 600, 5000, 30000 };
        var iterations = 12;
        var output = Path.Combine("artifacts", "performance", "m0-baseline.json");
        string? retainRoot = null;
        string? fixedRoot = null;
        string? retainedImageSource = null;
        var retainedImageCount = 1;
        var gate = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--counts" when index + 1 < args.Length:
                    counts = args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                        .Where(value => value > 0)
                        .Distinct()
                        .Order()
                        .ToArray();
                    break;
                case "--iterations" when index + 1 < args.Length:
                    iterations = Math.Clamp(int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture), 3, 100);
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--retain-root" when index + 1 < args.Length:
                    retainRoot = args[++index];
                    break;
                case "--fixed-root" when index + 1 < args.Length:
                    fixedRoot = args[++index];
                    break;
                case "--retained-image-source" when index + 1 < args.Length:
                    retainedImageSource = Path.GetFullPath(args[++index]);
                    break;
                case "--retained-image-count" when index + 1 < args.Length:
                    retainedImageCount = Math.Clamp(
                        int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture),
                        1,
                        5000);
                    break;
                case "--gate":
                    gate = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[index]}");
            }
        }

        if (counts.Length == 0)
        {
            throw new ArgumentException("At least one positive item count is required.");
        }
        if (fixedRoot is not null && counts.Length != 1)
        {
            throw new ArgumentException("--fixed-root requires exactly one --counts value.");
        }
        if (fixedRoot is not null && retainRoot is not null)
        {
            throw new ArgumentException("--fixed-root and --retain-root cannot be combined.");
        }
        if (retainedImageSource is not null && !File.Exists(retainedImageSource))
        {
            throw new FileNotFoundException("Retained benchmark image source was not found.", retainedImageSource);
        }
        if (retainRoot is null && fixedRoot is null && (retainedImageSource is not null || retainedImageCount != 1))
        {
            throw new ArgumentException("Retained image options require --retain-root.");
        }
        return new BenchmarkOptions(
            counts,
            iterations,
            output,
            retainRoot,
            fixedRoot,
            retainedImageSource,
            retainedImageCount,
            gate);
    }
}
