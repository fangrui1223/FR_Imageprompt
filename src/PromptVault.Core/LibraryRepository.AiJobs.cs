using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    public async Task<AiJobRecord> EnqueueAiJobAsync(
        AiJobInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.JobType);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ModelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.CacheKey);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_jobs(
                item_id, job_type, provider_id, model_version, status, priority,
                attempts, max_attempts, not_before, cache_key, payload, created_at, updated_at)
            VALUES(
                $item, $type, $provider, $version, 'queued', $priority,
                0, $maxAttempts, $notBefore, $cache, $payload, $now, $now)
            ON CONFLICT(cache_key) DO UPDATE SET
                priority = MAX(ai_jobs.priority, excluded.priority)
            RETURNING id, item_id, job_type, provider_id, model_version, status, priority,
                      attempts, max_attempts, not_before, cache_key, payload, last_error,
                      created_at, updated_at, completed_at;
            """;
        command.Parameters.AddWithValue("$item", input.ItemId);
        command.Parameters.AddWithValue("$type", input.JobType.Trim());
        command.Parameters.AddWithValue("$provider", input.ProviderId.Trim());
        command.Parameters.AddWithValue("$version", input.ModelVersion.Trim());
        command.Parameters.AddWithValue("$priority", input.Priority);
        command.Parameters.AddWithValue("$maxAttempts", Math.Clamp(input.MaxAttempts, 1, 20));
        command.Parameters.AddWithValue("$cache", input.CacheKey.Trim());
        command.Parameters.AddWithValue("$payload", string.IsNullOrWhiteSpace(input.Payload) ? "{}" : input.Payload);
        command.Parameters.AddWithValue("$notBefore", (input.NotBefore ?? now).ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadAiJob(reader);
    }

    public async Task<AiJobRecord?> ClaimNextAiJobAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_jobs
            SET status = 'running',
                attempts = attempts + 1,
                updated_at = $now,
                last_error = NULL
            WHERE id = (
                SELECT id FROM ai_jobs
                WHERE status = 'queued' AND not_before <= $now
                ORDER BY priority DESC, id
                LIMIT 1)
            RETURNING id, item_id, job_type, provider_id, model_version, status, priority,
                      attempts, max_attempts, not_before, cache_key, payload, last_error,
                      created_at, updated_at, completed_at;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAiJob(reader)
            : null;
    }

    public Task CompleteAiJobAsync(long jobId, CancellationToken cancellationToken = default) =>
        UpdateAiJobTerminalAsync(jobId, "completed", null, cancellationToken);

    public async Task FailAiJobAsync(
        long jobId,
        string error,
        DateTimeOffset retryAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_jobs
            SET status = CASE WHEN attempts < max_attempts THEN 'queued' ELSE 'failed' END,
                not_before = CASE WHEN attempts < max_attempts THEN $retryAt ELSE not_before END,
                last_error = $error,
                updated_at = $now
            WHERE id = $id AND status = 'running';
            """;
        command.Parameters.AddWithValue("$retryAt", retryAt.ToString("O"));
        command.Parameters.AddWithValue("$error", NormalizeAiJobError(error));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PauseAiJobsAsync(CancellationToken cancellationToken = default) =>
        await SetQueuedAiJobsStatusAsync("queued", "paused", cancellationToken).ConfigureAwait(false);

    public async Task<int> ResumeAiJobsAsync(CancellationToken cancellationToken = default) =>
        await SetQueuedAiJobsStatusAsync("paused", "queued", cancellationToken).ConfigureAwait(false);

    public async Task<int> RecoverInterruptedAiJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_jobs
            SET status = CASE WHEN attempts < max_attempts THEN 'queued' ELSE 'failed' END,
                not_before = $now,
                last_error = 'Worker exited before completing the task.',
                updated_at = $now
            WHERE status = 'running';
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AiJobRecord>> GetAiJobsAsync(
        AiJobStatus? status = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, item_id, job_type, provider_id, model_version, status, priority,
                   attempts, max_attempts, not_before, cache_key, payload, last_error,
                   created_at, updated_at, completed_at
            FROM ai_jobs
            WHERE $status IS NULL OR status = $status
            ORDER BY updated_at DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", status is { } value
            ? ToStorage(value)
            : DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var result = new List<AiJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadAiJob(reader));
        }
        return result;
    }

    private async Task UpdateAiJobTerminalAsync(
        long jobId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_jobs
            SET status = $status, last_error = $error, completed_at = $now, updated_at = $now
            WHERE id = $id AND status = 'running';
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> SetQueuedAiJobsStatusAsync(
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_jobs SET status = $to, updated_at = $now WHERE status = $from;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AiJobRecord ReadAiJob(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        ParseAiJobStatus(reader.GetString(5)),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        DateTimeOffset.Parse(reader.GetString(9)),
        reader.GetString(10),
        reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        DateTimeOffset.Parse(reader.GetString(13)),
        DateTimeOffset.Parse(reader.GetString(14)),
        reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)));

    private static string NormalizeAiJobError(string error)
    {
        var value = string.IsNullOrWhiteSpace(error) ? "Unknown AI worker error." : error.Trim();
        return value.Length <= 1000 ? value : value[..1000];
    }

    private static string ToStorage(AiJobStatus status) => status.ToString().ToLowerInvariant();

    private static AiJobStatus ParseAiJobStatus(string value) => value switch
    {
        "queued" => AiJobStatus.Queued,
        "running" => AiJobStatus.Running,
        "paused" => AiJobStatus.Paused,
        "completed" => AiJobStatus.Completed,
        "failed" => AiJobStatus.Failed,
        _ => throw new InvalidDataException($"未知 AI 任务状态：{value}")
    };
}
