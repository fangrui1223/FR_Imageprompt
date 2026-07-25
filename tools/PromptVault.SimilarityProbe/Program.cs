using System.Diagnostics;
using System.Text.Json;
using PromptVault.Core;

const int count = 30_000;
const int dimension = 512;
const int iterations = 31;
var random = new Random(42);
var now = DateTimeOffset.UtcNow;
var embeddings = Enumerable.Range(1, count).Select(id =>
{
    var vector = new float[dimension];
    for (var i = 0; i < vector.Length; i++) vector[i] = random.NextSingle() - 0.5f;
    return new ImageEmbeddingRecord(id, "probe", "1", vector, now, now);
}).ToArray();
var build = Stopwatch.StartNew();
var index = new SimilarityIndex(embeddings);
build.Stop();
var query = embeddings[123].Vector;
_ = index.Search(query, 100);
var samples = new double[iterations];
for (var i = 0; i < samples.Length; i++)
{
    var stopwatch = Stopwatch.StartNew();
    _ = index.Search(query, 100);
    stopwatch.Stop();
    samples[i] = stopwatch.Elapsed.TotalMilliseconds;
}
Array.Sort(samples);
var report = new
{
    count,
    dimension,
    buildMs = build.Elapsed.TotalMilliseconds,
    iterations,
    p50Ms = samples[(int)Math.Ceiling(samples.Length * 0.50) - 1],
    p95Ms = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1],
    maxMs = samples[^1],
    thresholdMs = 100,
    passed = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1] <= 100
};
Console.WriteLine(JsonSerializer.Serialize(report));
return report.passed ? 0 : 1;
