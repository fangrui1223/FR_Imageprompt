using System.Numerics;

namespace PromptVault.Core;

public sealed class SimilarityIndex
{
    private readonly long[] _itemIds;
    private readonly float[][] _vectors;
    private readonly int _dimension;

    public SimilarityIndex(IEnumerable<ImageEmbeddingRecord> embeddings)
    {
        var entries = embeddings.ToArray();
        _dimension = entries.Length == 0 ? 0 : entries[0].Vector.Length;
        if (entries.Any(entry => entry.Vector.Length != _dimension))
        {
            throw new ArgumentException("相似索引中的向量维度必须一致。", nameof(embeddings));
        }
        _itemIds = entries.Select(entry => entry.ItemId).ToArray();
        _vectors = entries.Select(entry =>
        {
            var vector = entry.Vector.ToArray();
            NormalizeInPlace(vector);
            return vector;
        }).ToArray();
    }

    public int Count => _itemIds.Length;
    public int Dimension => _dimension;

    public IReadOnlyList<SimilarityMatch> Search(
        ReadOnlySpan<float> query,
        int limit = 100,
        long? excludeItemId = null)
    {
        if (_dimension == 0) return [];
        if (query.Length != _dimension)
        {
            throw new ArgumentException($"查询向量维度应为 {_dimension}。", nameof(query));
        }
        var normalized = query.ToArray();
        NormalizeInPlace(normalized);
        var top = new PriorityQueue<SimilarityMatch, float>();
        var requested = Math.Clamp(limit, 1, Math.Min(1000, _itemIds.Length));
        for (var i = 0; i < _itemIds.Length; i++)
        {
            if (_itemIds[i] == excludeItemId) continue;
            var score = Dot(normalized, _vectors[i]);
            var match = new SimilarityMatch(_itemIds[i], score);
            if (top.Count < requested)
            {
                top.Enqueue(match, score);
            }
            else if (top.TryPeek(out _, out var minimum) && score > minimum)
            {
                top.Dequeue();
                top.Enqueue(match, score);
            }
        }
        return top.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ItemId)
            .ToArray();
    }

    public static void NormalizeInPlace(float[] values)
    {
        double sum = 0;
        foreach (var value in values) sum += value * value;
        var norm = Math.Sqrt(sum);
        if (norm <= 0) throw new ArgumentException("向量范数必须大于零。", nameof(values));
        var inverse = (float)(1d / norm);
        for (var i = 0; i < values.Length; i++) values[i] *= inverse;
    }

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var width = Vector<float>.Count;
        var sum = Vector<float>.Zero;
        var index = 0;
        for (; index <= left.Length - width; index += width)
        {
            sum += new Vector<float>(left.Slice(index, width))
                   * new Vector<float>(right.Slice(index, width));
        }
        var result = Vector.Dot(sum, Vector<float>.One);
        for (; index < left.Length; index++) result += left[index] * right[index];
        return result;
    }
}
