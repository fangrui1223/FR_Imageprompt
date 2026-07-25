using System.Diagnostics;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class SimilarityIndexTests
{
    [Fact]
    public void SearchReturnsNearestVectorsAndExcludesSource()
    {
        var now = DateTimeOffset.UtcNow;
        var index = new SimilarityIndex([
            new ImageEmbeddingRecord(1, "p", "v", [1, 0, 0], now, now),
            new ImageEmbeddingRecord(2, "p", "v", [0.9f, 0.1f, 0], now, now),
            new ImageEmbeddingRecord(3, "p", "v", [0, 1, 0], now, now)
        ]);

        var result = index.Search([1, 0, 0], 2, excludeItemId: 1);

        Assert.Equal([2L, 3L], result.Select(item => item.ItemId));
        Assert.True(result[0].Score > result[1].Score);
    }

    [Fact]
    public void ThirtyThousandVectorSearchStaysWithinGuardrail()
    {
        const int count = 30_000;
        const int dimension = 512;
        var now = DateTimeOffset.UtcNow;
        var random = new Random(42);
        var embeddings = Enumerable.Range(1, count).Select(id =>
        {
            var vector = new float[dimension];
            for (var i = 0; i < vector.Length; i++) vector[i] = random.NextSingle() - 0.5f;
            return new ImageEmbeddingRecord(id, "benchmark", "1", vector, now, now);
        }).ToArray();
        var index = new SimilarityIndex(embeddings);
        var query = embeddings[123].Vector;
        _ = index.Search(query, 100);

        var stopwatch = Stopwatch.StartNew();
        var result = index.Search(query, 100);
        stopwatch.Stop();

        Assert.Equal(100, result.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"30,000-vector debug guardrail took {stopwatch.Elapsed.TotalMilliseconds:F3}ms.");
    }
}
