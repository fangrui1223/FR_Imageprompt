using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    public async Task UpsertImageEmbeddingAsync(
        long itemId,
        string providerId,
        string modelVersion,
        ReadOnlyMemory<float> vector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        if (vector.Length == 0) throw new ArgumentException("图片向量不能为空。", nameof(vector));
        var normalized = vector.ToArray();
        SimilarityIndex.NormalizeInPlace(normalized);
        var bytes = new byte[normalized.Length * sizeof(float)];
        Buffer.BlockCopy(normalized, 0, bytes, 0, bytes.Length);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO image_embeddings(
                item_id, provider_id, model_version, dimension, vector, created_at, updated_at)
            VALUES($item, $provider, $version, $dimension, $vector, $now, $now)
            ON CONFLICT(item_id, provider_id, model_version) DO UPDATE SET
                dimension = excluded.dimension,
                vector = excluded.vector,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$provider", providerId.Trim());
        command.Parameters.AddWithValue("$version", modelVersion.Trim());
        command.Parameters.AddWithValue("$dimension", normalized.Length);
        command.Parameters.Add("$vector", SqliteType.Blob).Value = bytes;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageEmbeddingRecord>> LoadImageEmbeddingsAsync(
        string providerId,
        string modelVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, provider_id, model_version, dimension, vector, created_at, updated_at
            FROM image_embeddings
            WHERE provider_id = $provider AND model_version = $version
            ORDER BY item_id;
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$version", modelVersion);
        var result = new List<ImageEmbeddingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dimension = reader.GetInt32(3);
            var bytes = (byte[])reader[4];
            if (bytes.Length != dimension * sizeof(float))
            {
                throw new InvalidDataException($"图片 {reader.GetInt64(0)} 的向量数据已损坏。");
            }
            var vector = new float[dimension];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            result.Add(new ImageEmbeddingRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                vector,
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return result;
    }

    public async Task<int> RebuildImageEmbeddingIndexAsync(
        string providerId,
        string modelVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM image_embeddings
            WHERE provider_id = $provider AND model_version = $version;
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$version", modelVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
