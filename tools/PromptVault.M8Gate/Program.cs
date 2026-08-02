using System.Runtime.InteropServices;
using System.Text.Json;

var options = Options.Parse(args);
var inputs = options.Inputs.ToDictionary(
    pair => pair.Key,
    pair => JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(pair.Value))),
    StringComparer.OrdinalIgnoreCase);
try
{
    var checks = new List<object>();
    var allPassed = true;
    foreach (var pair in inputs)
    {
        var root = pair.Value.RootElement;
        var passed = ReadBoolean(root, "Passed") ?? ReadBoolean(root, "passed") ?? false;
        checks.Add(new { Name = pair.Key, Passed = passed, Source = Path.GetFileName(options.Inputs[pair.Key]) });
        allPassed &= passed;
    }

    var frame = inputs["frame"].RootElement.GetProperty("Board");
    var fixture = inputs["fixture"].RootElement.GetProperty("Fixtures")[2];
    var m5 = inputs["m5"].RootElement;
    var media = inputs["media"].RootElement;
    var comparison = new
    {
        M8FiveThousand = new
        {
            ViewportQueryP95Ms = fixture.GetProperty("ViewportQueryP95Ms").GetDouble(),
            BaselineM8_00P95Ms = 0.0570,
            LimitMs = 0.10,
            MaximumCandidates = fixture.GetProperty("MaximumRealizedCandidateCount").GetInt32()
        },
        M5Regression = new
        {
            ViewportQueryP95Ms = m5.GetProperty("viewportQueryP95Ms").GetDouble(),
            BaselineP95Ms = 0.0558,
            LimitMs = 0.10,
            MaximumCandidates = m5.GetProperty("maximumRealizedCandidateCount").GetInt32()
        },
        RealWpfFrames = new
        {
            P50Ms = frame.GetProperty("FrameP50Ms").GetDouble(),
            P95Ms = frame.GetProperty("FrameP95Ms").GetDouble(),
            P99Ms = frame.GetProperty("FrameP99Ms").GetDouble(),
            MaximumMs = frame.GetProperty("FrameMaximumMs").GetDouble(),
            OneRefreshLimitMs = 16.949,
            TwoRefreshLimitMs = 33.898,
            MaximumRealizedElements = frame.GetProperty("MaximumRealizedElements").GetInt32(),
            NearBlackFrames = frame.GetProperty("NearBlackFrames").GetInt32()
        },
        FourKMedia = new
        {
            FixtureBytes = media.GetProperty("Fixture").GetProperty("Bytes").GetInt64(),
            FirstDecodeMs = media.GetProperty("LargeImage").GetProperty("FirstDecodeMs").GetDouble(),
            CachedSwitchMs = media.GetProperty("LargeImage").GetProperty("CachedSwitchMs").GetDouble(),
            CacheBytes = media.GetProperty("LargeImage").GetProperty("ImmersiveCache").GetProperty("CachedBytes").GetInt64(),
            CacheBudgetBytes = media.GetProperty("LargeImage").GetProperty("ImmersiveCache").GetProperty("MemoryBudgetBytes").GetInt64()
        }
    };
    var report = new
    {
        Milestone = "M8-pureref-board-aggregate-gate",
        GeneratedAt = DateTimeOffset.Now,
        Environment = new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Display = "3840x2560 @ 150% / 59Hz"
        },
        Checks = checks,
        Comparison = comparison,
        DataSafety = new
        {
            SyntheticFixturesOnly = true,
            ExplicitSettingsUsed = true,
            CaptureListeningEnabled = false,
            CaptureQuickEditEnabled = false,
            OnlineAiEnabled = false,
            RealLibraryOpened = false,
            RealLibraryModified = false,
            RealLibraryMigrated = false,
            RealLibraryDeleted = false,
            ClipboardUsed = false,
            NetworkUsed = false,
            ApiKeysUsed = false,
            AbsolutePathsSerialized = false
        },
        Passed = allPassed
    };
    var output = Path.GetFullPath(options.Output);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(output, json);
    Console.WriteLine(json);
    return allPassed ? 0 : 2;
}
finally
{
    foreach (var document in inputs.Values) document.Dispose();
}

static bool? ReadBoolean(JsonElement root, string property) =>
    root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean()
        : null;

sealed record Options(string Output, IReadOnlyDictionary<string, string> Inputs)
{
    private static readonly string[] Required =
        ["fixture", "camera", "input", "command", "chrome", "inspector", "transform", "image", "frame", "m5", "m7", "media"];

    public static Options Parse(IReadOnlyList<string> args)
    {
        string? output = null;
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var key = args[index].TrimStart('-').ToLowerInvariant();
            if (++index >= args.Count) throw new ArgumentException($"Missing value for --{key}.");
            if (key == "output") output = args[index];
            else inputs[key] = args[index];
        }
        if (output is null) throw new ArgumentException("--output is required.");
        var missing = Required.Where(key => !inputs.ContainsKey(key)).ToArray();
        if (missing.Length > 0) throw new ArgumentException($"Missing inputs: {string.Join(", ", missing)}");
        return new Options(output, inputs);
    }
}
