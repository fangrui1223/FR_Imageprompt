using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    public async Task<MetadataCandidateRecord> UpsertMetadataCandidateAsync(
        MetadataCandidateInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateMetadata(input.FieldType, input.Value);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata_candidates(
                item_id, field_type, value, source, provider_id, model_name, model_version,
                confidence, status, created_at, updated_at)
            VALUES(
                $item, $field, $value, $source, $provider, $model, $version,
                $confidence, 'pending', $now, $now)
            ON CONFLICT(item_id, field_type, source, provider_id, model_version) DO UPDATE SET
                value = CASE WHEN metadata_candidates.status = 'pending' THEN excluded.value ELSE metadata_candidates.value END,
                confidence = CASE WHEN metadata_candidates.status = 'pending' THEN excluded.confidence ELSE metadata_candidates.confidence END,
                model_name = CASE WHEN metadata_candidates.status = 'pending' THEN excluded.model_name ELSE metadata_candidates.model_name END,
                updated_at = CASE WHEN metadata_candidates.status = 'pending' THEN excluded.updated_at ELSE metadata_candidates.updated_at END
            RETURNING id, item_id, field_type, value, source, provider_id, model_name,
                      model_version, confidence, status, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$item", input.ItemId);
        command.Parameters.AddWithValue("$field", input.FieldType.Trim());
        command.Parameters.AddWithValue("$value", input.Value.Trim());
        command.Parameters.AddWithValue("$source", ToStorage(input.Source));
        command.Parameters.AddWithValue("$provider", input.ProviderId.Trim());
        command.Parameters.AddWithValue("$model", input.ModelName.Trim());
        command.Parameters.AddWithValue("$version", input.ModelVersion.Trim());
        command.Parameters.AddWithValue("$confidence", (object?)input.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadCandidate(reader);
    }

    public async Task<IReadOnlyList<MetadataCandidateRecord>> GetMetadataCandidatesAsync(
        long itemId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, item_id, field_type, value, source, provider_id, model_name,
                   model_version, confidence, status, created_at, updated_at
            FROM metadata_candidates
            WHERE item_id = $item
            ORDER BY CASE status WHEN 'pending' THEN 0 ELSE 1 END, field_type, id;
            """;
        command.Parameters.AddWithValue("$item", itemId);
        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MetadataCandidateRecord>> GetPendingMetadataCandidatesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, item_id, field_type, value, source, provider_id, model_name,
                   model_version, confidence, status, created_at, updated_at
            FROM metadata_candidates
            WHERE status = 'pending'
            ORDER BY updated_at, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserMetadataRecord> ConfirmMetadataCandidateAsync(
        long candidateId,
        string? editedValue = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await ReadCandidateAsync(
            connection,
            transaction,
            candidateId,
            cancellationToken).ConfigureAwait(false);
        if (candidate.Status == MetadataCandidateStatus.Rejected)
        {
            throw new InvalidOperationException("已拒绝的 AI 草稿不能直接确认。");
        }
        var value = string.IsNullOrWhiteSpace(editedValue) ? candidate.Value : editedValue.Trim();
        ValidateMetadata(candidate.FieldType, value);
        var status = string.Equals(value, candidate.Value, StringComparison.Ordinal)
            ? MetadataCandidateStatus.Confirmed
            : MetadataCandidateStatus.Modified;
        var now = DateTimeOffset.UtcNow;

        if (string.Equals(candidate.FieldType, "category", StringComparison.OrdinalIgnoreCase))
        {
            var findCategory = connection.CreateCommand();
            findCategory.Transaction = transaction;
            findCategory.CommandText = """
                SELECT id
                FROM categories
                WHERE name = $name COLLATE NOCASE AND is_enabled = 1
                LIMIT 1;
                """;
            findCategory.Parameters.AddWithValue("$name", value);
            var categoryId = await findCategory.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (categoryId is null)
            {
                throw new InvalidOperationException(
                    $"AI 分类“{value}”无法映射到现有分类，请先人工选择主分类。");
            }

            var applyCategory = connection.CreateCommand();
            applyCategory.Transaction = transaction;
            applyCategory.CommandText = """
                UPDATE collection_items
                SET category_id = $category, updated_at = $now
                WHERE id = $item AND deleted_at IS NULL;
                """;
            applyCategory.Parameters.AddWithValue("$category", (long)categoryId);
            applyCategory.Parameters.AddWithValue("$now", now.ToString("O"));
            applyCategory.Parameters.AddWithValue("$item", candidate.ItemId);
            var changed = await applyCategory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                throw new InvalidOperationException("AI 分类对应的图片已不可编辑。");
            }
        }

        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO user_metadata(
                item_id, field_type, value, source_candidate_id, created_at, updated_at)
            VALUES($item, $field, $value, $candidate, $now, $now)
            ON CONFLICT(item_id, field_type) DO UPDATE SET
                value = excluded.value,
                source_candidate_id = excluded.source_candidate_id,
                updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$item", candidate.ItemId);
        upsert.Parameters.AddWithValue("$field", candidate.FieldType);
        upsert.Parameters.AddWithValue("$value", value);
        upsert.Parameters.AddWithValue("$candidate", candidate.Id);
        upsert.Parameters.AddWithValue("$now", now.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await UpdateCandidateStatusAsync(
            connection,
            transaction,
            candidate.Id,
            status,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetUserMetadataAsync(candidate.ItemId, candidate.FieldType, cancellationToken)
            .ConfigureAwait(false))!;
    }

    public async Task RejectMetadataCandidateAsync(
        long candidateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await ReadCandidateAsync(
            connection,
            transaction,
            candidateId,
            cancellationToken).ConfigureAwait(false);
        await UpdateCandidateStatusAsync(
            connection,
            transaction,
            candidate.Id,
            MetadataCandidateStatus.Rejected,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserMetadataRecord?> GetUserMetadataAsync(
        long itemId,
        string fieldType,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, field_type, value, source_candidate_id, created_at, updated_at
            FROM user_metadata
            WHERE item_id = $item AND field_type = $field
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$field", fieldType.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new UserMetadataRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5)))
            : null;
    }

    private static async Task<MetadataCandidateRecord> ReadCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long candidateId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, item_id, field_type, value, source, provider_id, model_name,
                   model_version, confidence, status, created_at, updated_at
            FROM metadata_candidates
            WHERE id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"找不到 AI 草稿 {candidateId}。");
        }
        return ReadCandidate(reader);
    }

    private static async Task<IReadOnlyList<MetadataCandidateRecord>> ReadCandidatesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<MetadataCandidateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadCandidate(reader));
        }
        return result;
    }

    private static MetadataCandidateRecord ReadCandidate(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        ParseSource(reader.GetString(4)),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetDouble(8),
        ParseStatus(reader.GetString(9)),
        DateTimeOffset.Parse(reader.GetString(10)),
        DateTimeOffset.Parse(reader.GetString(11)));

    private static async Task UpdateCandidateStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long candidateId,
        MetadataCandidateStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE metadata_candidates
            SET status = $status, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", ToStorage(status));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", candidateId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateMetadata(string fieldType, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldType);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (fieldType.Trim().Length > 80) throw new ArgumentOutOfRangeException(nameof(fieldType));
        if (value.Trim().Length > 8000) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static string ToStorage(AiMetadataSource source) => source switch
    {
        AiMetadataSource.User => "user",
        AiMetadataSource.LocalModel => "local_model",
        AiMetadataSource.OnlineApi => "online_api",
        AiMetadataSource.Rule => "rule",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static AiMetadataSource ParseSource(string value) => value switch
    {
        "user" => AiMetadataSource.User,
        "local_model" => AiMetadataSource.LocalModel,
        "online_api" => AiMetadataSource.OnlineApi,
        "rule" => AiMetadataSource.Rule,
        _ => throw new InvalidDataException($"未知 AI 元数据来源：{value}")
    };

    private static string ToStorage(MetadataCandidateStatus status) => status switch
    {
        MetadataCandidateStatus.Pending => "pending",
        MetadataCandidateStatus.Confirmed => "confirmed",
        MetadataCandidateStatus.Modified => "modified",
        MetadataCandidateStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static MetadataCandidateStatus ParseStatus(string value) => value switch
    {
        "pending" => MetadataCandidateStatus.Pending,
        "confirmed" => MetadataCandidateStatus.Confirmed,
        "modified" => MetadataCandidateStatus.Modified,
        "rejected" => MetadataCandidateStatus.Rejected,
        _ => throw new InvalidDataException($"未知 AI 草稿状态：{value}")
    };
}
