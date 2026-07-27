using System.Text.Json;

var options = GateOptions.Parse(args);
var checks = new List<GateCheck>();

using (var benchmark = JsonDocument.Parse(File.ReadAllText(options.BenchmarkPath)))
{
    var scale = benchmark.RootElement.GetProperty("Scales")
        .EnumerateArray()
        .Single(element => element.GetProperty("ItemCount").GetInt32() == 30_000);
    Add(checks, "startup.repository-p95", scale.GetProperty("RepositoryInitialize").GetProperty("P95Ms").GetDouble(), 1500);
    foreach (var scenario in scale.GetProperty("Scenarios").EnumerateArray())
    {
        Add(
            checks,
            $"database.{scenario.GetProperty("Name").GetString()}-p95",
            scenario.GetProperty("P95Ms").GetDouble(),
            80);
    }
}

using (var performance = JsonDocument.Parse(File.ReadAllText(options.PerformancePath)))
{
    var root = performance.RootElement;
    var performanceChecks = root.GetProperty("Checks");
    Add(checks, "images.first-4k-decode", ReadActual(performanceChecks, "FirstLargeDecode", "ActualMs"), 300);
    Add(checks, "images.cached-immersive-switch", ReadActual(performanceChecks, "CachedLargeSwitch", "ActualMs"), 100);
    Add(
        checks,
        "memory.thumbnail-cache",
        ReadActual(performanceChecks, "ThumbnailMemory", "ActualBytes"),
        512L * 1024 * 1024,
        "bytes");
    var immersive = performanceChecks.GetProperty("ImmersiveMemory");
    Add(
        checks,
        "memory.immersive-cache",
        immersive.GetProperty("ActualBytes").GetDouble(),
        immersive.GetProperty("LimitBytes").GetDouble(),
        "bytes");
}

using (var virtualization = JsonDocument.Parse(File.ReadAllText(options.VirtualizationPath)))
{
    var root = virtualization.RootElement;
    var layout = root.GetProperty("Layout");
    var rowCount = root.GetProperty("Environment").GetProperty("RowCount").GetDouble();
    Add(checks, "virtualization.realized-containers", layout.GetProperty("RealizedContainers").GetDouble(), rowCount / 10, "containers");
    if (options.TracePath is null)
    {
        var frames = root.GetProperty("Frames");
        Add(checks, "scroll.proxy-p95", frames.GetProperty("P95Ms").GetDouble(), 16.949);
        Add(checks, "scroll.proxy-p99", frames.GetProperty("P99Ms").GetDouble(), 33.898);
        Add(checks, "scroll.proxy-maximum", frames.GetProperty("MaximumMs").GetDouble(), 33.898);
    }
}

using (var similarity = JsonDocument.Parse(File.ReadAllText(options.SimilarityPath)))
{
    Add(checks, "similarity.p95", similarity.RootElement.GetProperty("p95Ms").GetDouble(), 100);
}

if (options.TracePath is not null)
{
    EvaluateUiTrace(options.TracePath, checks);
}

if (options.SimulateRegression)
{
    checks.Add(new GateCheck(
        "regression-sentinel",
        81,
        80,
        "ms",
        false,
        "Deliberate one-millisecond regression used to verify failure propagation."));
}

var report = new
{
    milestone = "M6-03",
    evaluatedAtUtc = DateTimeOffset.UtcNow,
    thresholds = new
    {
        coldStartupMs = 1500,
        databaseP95Ms = 80,
        first4kDecodeMs = 300,
        cachedImmersiveMs = 100,
        scrollP95Ms = 16.949,
        scrollP99AndMaximumMs = 33.898,
        similarityP95Ms = 100,
        thumbnailCacheBytes = 512L * 1024 * 1024
    },
    checks,
    passed = checks.Count > 0 && checks.All(check => check.Passed),
    simulatedRegression = options.SimulateRegression
};
Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
File.WriteAllText(
    options.OutputPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return report.passed ? 0 : 2;

static double ReadActual(JsonElement checks, string group, string name) =>
    checks.GetProperty(group).GetProperty(name).GetDouble();

static void Add(
    List<GateCheck> checks,
    string name,
    double actual,
    double limit,
    string unit = "ms",
    string? detail = null) =>
    checks.Add(new GateCheck(name, actual, limit, unit, actual <= limit, detail));

static void EvaluateUiTrace(string tracePath, List<GateCheck> checks)
{
    var entries = File.ReadLines(tracePath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonDocument.Parse(line))
        .ToArray();
    try
    {
        var shown = entries.LastOrDefault(document =>
            document.RootElement.GetProperty("name").GetString() == "main-window-shown");
        if (shown is not null)
        {
            Add(
                checks,
                "ui.cold-main-window-shown",
                shown.RootElement.GetProperty("processElapsedMs").GetDouble(),
                1500);
        }
        else
        {
            checks.Add(GateCheck.Missing("ui.cold-main-window-shown"));
        }

        var scroll = entries.LastOrDefault(document =>
            document.RootElement.GetProperty("name").GetString() == "ui-frame-sample"
            && document.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("interaction", out var interaction)
            && interaction.GetString() == "gallery-scroll");
        if (scroll is not null)
        {
            var data = scroll.RootElement.GetProperty("data");
            Add(checks, "ui.scroll-p95", data.GetProperty("p95Ms").GetDouble(), 16.949);
            Add(checks, "ui.scroll-p99", data.GetProperty("p99Ms").GetDouble(), 33.898);
            Add(checks, "ui.scroll-maximum", data.GetProperty("maximumMs").GetDouble(), 33.898);
        }
        else
        {
            checks.Add(GateCheck.Missing("ui.scroll-sample"));
        }

        var transitions = entries.Where(document =>
                document.RootElement.GetProperty("name").GetString() == "gallery-refresh-transition")
            .Select(document => document.RootElement.GetProperty("data"))
            .Where(data => data.GetProperty("displayedItems").GetInt32() > 0)
            .ToArray();
        var minimumOpacity = transitions.Length == 0
            ? 1
            : transitions.Min(data => data.GetProperty("rowsOpacity").GetDouble());
        checks.Add(new GateCheck(
            "ui.old-frame-remains-visible",
            minimumOpacity,
            0.02,
            "minimum opacity",
            minimumOpacity >= 0.02,
            "This is a lower-bound check; values at or below 0.02 indicate a near-black transition."));

        var completed = entries.Any(document =>
            document.RootElement.GetProperty("name").GetString() == "gallery-scroll-gate-complete");
        if (!completed) checks.Add(GateCheck.Missing("ui.scroll-gate-complete"));
    }
    finally
    {
        foreach (var entry in entries) entry.Dispose();
    }
}

internal sealed record GateCheck(
    string Name,
    double Actual,
    double Limit,
    string Unit,
    bool Passed,
    string? Detail)
{
    public static GateCheck Missing(string name) =>
        new(name, double.PositiveInfinity, 0, "missing", false, "Required measurement was not present.");
}

internal sealed record GateOptions(
    string BenchmarkPath,
    string PerformancePath,
    string VirtualizationPath,
    string SimilarityPath,
    string? TracePath,
    string OutputPath,
    bool SimulateRegression)
{
    public static GateOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var simulate = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--simulate-regression", StringComparison.OrdinalIgnoreCase))
            {
                simulate = true;
                continue;
            }
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Unknown or incomplete argument: {args[index]}");
            }
            values[args[index]] = args[++index];
        }

        string Required(string name) => Path.GetFullPath(
            values.TryGetValue(name, out var value)
                ? value
                : throw new ArgumentException($"{name} is required."));
        return new GateOptions(
            Required("--benchmark"),
            Required("--performance"),
            Required("--virtualization"),
            Required("--similarity"),
            values.TryGetValue("--trace", out var trace) ? Path.GetFullPath(trace) : null,
            Required("--output"),
            simulate);
    }
}
