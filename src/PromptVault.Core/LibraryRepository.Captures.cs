using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    public async Task<CaptureSessionRecord> CreateCaptureSessionAsync(
        CaptureSessionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.StagedOriginalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Extension);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO capture_inbox(
                id, state, staged_original_path, extension, captured_at, updated_at,
                prompt_deadline_at, last_clipboard_sequence)
            VALUES(
                $id, $state, $original, $extension, $captured, $captured,
                $deadline, $sequence);
            """;
        command.Parameters.AddWithValue("$id", input.Id.ToString("D"));
        command.Parameters.AddWithValue("$state", (int)CaptureState.ImageDetected);
        command.Parameters.AddWithValue("$original", input.StagedOriginalPath);
        command.Parameters.AddWithValue("$extension", input.Extension.TrimStart('.').ToLowerInvariant());
        command.Parameters.AddWithValue("$captured", input.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$deadline", input.PromptDeadlineAt.ToString("O"));
        command.Parameters.AddWithValue("$sequence", input.ClipboardSequence is { } sequence
            ? sequence
            : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return (await GetCaptureSessionAsync(input.Id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<CaptureSessionRecord?> GetCaptureSessionAsync(
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = BuildCaptureCommand(connection);
        command.CommandText += " WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCaptureSession(reader)
            : null;
    }

    public async Task<IReadOnlyList<CaptureSessionRecord>> GetActiveCaptureSessionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = BuildCaptureCommand(connection);
        command.CommandText += """
             WHERE state IN ($imageDetected, $preparing, $waiting, $debouncing, $needsPrompt, $failed)
                OR (state IN ($saved, $waitingForAi) AND undo_deadline_at >= $now)
             ORDER BY captured_at, id;
            """;
        command.Parameters.AddWithValue("$imageDetected", (int)CaptureState.ImageDetected);
        command.Parameters.AddWithValue("$preparing", (int)CaptureState.PreparingImage);
        command.Parameters.AddWithValue("$waiting", (int)CaptureState.WaitingForPrompt);
        command.Parameters.AddWithValue("$debouncing", (int)CaptureState.PromptDebouncing);
        command.Parameters.AddWithValue("$needsPrompt", (int)CaptureState.NeedsPrompt);
        command.Parameters.AddWithValue("$failed", (int)CaptureState.Failed);
        command.Parameters.AddWithValue("$saved", (int)CaptureState.Saved);
        command.Parameters.AddWithValue("$waitingForAi", (int)CaptureState.WaitingForAi);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return await ReadCaptureSessionsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CaptureSessionRecord>> GetCaptureInboxAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = BuildCaptureCommand(connection);
        command.CommandText += """
             WHERE state IN ($needsPrompt, $failed)
             ORDER BY captured_at, id;
            """;
        command.Parameters.AddWithValue("$needsPrompt", (int)CaptureState.NeedsPrompt);
        command.Parameters.AddWithValue("$failed", (int)CaptureState.Failed);
        return await ReadCaptureSessionsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CaptureSessionRecord> TransitionCaptureAsync(
        Guid captureId,
        CaptureState target,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadCaptureStateAsync(
            connection,
            transaction,
            captureId,
            cancellationToken).ConfigureAwait(false);
        CaptureStateMachine.EnsureTransition(current, target);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE capture_inbox
            SET state = $state, error = $error, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", (int)target);
        command.Parameters.AddWithValue("$error", (object?)NormalizeError(error) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetCaptureSessionAsync(captureId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<CaptureSessionRecord> MarkCapturePreparedAsync(
        Guid captureId,
        PreparedCaptureInput input,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadCaptureSessionAsync(
            connection,
            transaction,
            captureId,
            cancellationToken).ConfigureAwait(false);
        var target = now >= current.PromptDeadlineAt
            ? CaptureState.NeedsPrompt
            : CaptureState.WaitingForPrompt;
        CaptureStateMachine.EnsureTransition(current.State, target);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE capture_inbox
            SET state = $state,
                staged_small_path = $small,
                staged_medium_path = $medium,
                hash = $hash,
                format = $format,
                width = $width,
                height = $height,
                error = NULL,
                updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", (int)target);
        command.Parameters.AddWithValue("$small", input.StagedSmallPath);
        command.Parameters.AddWithValue("$medium", input.StagedMediumPath);
        command.Parameters.AddWithValue("$hash", input.Hash);
        command.Parameters.AddWithValue("$format", input.Format);
        command.Parameters.AddWithValue("$width", input.Width);
        command.Parameters.AddWithValue("$height", input.Height);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetCaptureSessionAsync(captureId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<CaptureSessionRecord> SetCapturePromptAsync(
        Guid captureId,
        string prompt,
        uint? clipboardSequence = null,
        CancellationToken cancellationToken = default)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0) throw new ArgumentException("提示词不能为空。", nameof(prompt));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadCaptureStateAsync(
            connection,
            transaction,
            captureId,
            cancellationToken).ConfigureAwait(false);
        CaptureStateMachine.EnsureTransition(current, CaptureState.PromptDebouncing);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE capture_inbox
            SET state = $state,
                prompt = $prompt,
                error = NULL,
                last_clipboard_sequence = COALESCE($sequence, last_clipboard_sequence),
                updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", (int)CaptureState.PromptDebouncing);
        command.Parameters.AddWithValue("$prompt", prompt);
        command.Parameters.AddWithValue("$sequence", clipboardSequence is { } sequence
            ? sequence
            : DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetCaptureSessionAsync(captureId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<CaptureSessionRecord> UpdateCaptureDraftAsync(
        Guid captureId,
        string prompt,
        string notes,
        long? categoryId,
        string tags,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capture_inbox
            SET prompt = $prompt,
                notes = $notes,
                category_id = $category,
                tags = $tags,
                updated_at = $now
            WHERE id = $id AND state NOT IN ($saved, $waitingForAi, $undone);
            """;
        command.Parameters.AddWithValue("$prompt", prompt.Trim());
        command.Parameters.AddWithValue("$notes", notes.Trim());
        command.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", tags.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        command.Parameters.AddWithValue("$saved", (int)CaptureState.Saved);
        command.Parameters.AddWithValue("$waitingForAi", (int)CaptureState.WaitingForAi);
        command.Parameters.AddWithValue("$undone", (int)CaptureState.Undone);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("捕获项目已经结束，不能再修改。");
        }
        return (await GetCaptureSessionAsync(captureId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<CaptureSaveResult> SaveCaptureAsync(
        Guid captureId,
        SaveItemInput input,
        DateTimeOffset undoDeadlineAt,
        CancellationToken cancellationToken = default,
        bool persistStagedFiles = false)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var capture = await ReadCaptureSessionAsync(
            connection,
            transaction,
            captureId,
            cancellationToken).ConfigureAwait(false);
        CaptureStateMachine.EnsureTransition(capture.State, CaptureState.Saved);

        if (persistStagedFiles)
        {
            input = input with
            {
                Asset = await PersistCaptureFilesAsync(connection, transaction, capture, input.Asset, cancellationToken)
                    .ConfigureAwait(false)
            };
        }

        var previous = await ReadExistingItemSnapshotAsync(
            connection,
            transaction,
            input.Asset.Hash,
            cancellationToken).ConfigureAwait(false);
        var result = await SaveItemCoreAsync(connection, transaction, input, cancellationToken).ConfigureAwait(false);

        var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandText = "INSERT OR REPLACE INTO capture_undo_guards(capture_id, fingerprint) VALUES($capture, $fingerprint);";
        guard.Parameters.AddWithValue("$capture", captureId.ToString("D"));
        guard.Parameters.AddWithValue("$fingerprint", await CaptureItemFingerprintAsync(
            connection, transaction, result.ItemId, cancellationToken).ConfigureAwait(false));
        await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE capture_inbox
            SET state = $state,
                prompt = $prompt,
                notes = $notes,
                category_id = $category,
                tags = $tags,
                saved_item_id = $item,
                was_duplicate = $duplicate,
                undo_deadline_at = $undoDeadline,
                previous_prompt = $previousPrompt,
                previous_notes = $previousNotes,
                previous_category_id = $previousCategory,
                previous_tags = $previousTags,
                previous_deleted_at = $previousDeletedAt,
                error = NULL,
                updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", (int)CaptureState.Saved);
        command.Parameters.AddWithValue("$prompt", input.Prompt.Trim());
        command.Parameters.AddWithValue("$notes", input.Notes.Trim());
        command.Parameters.AddWithValue("$category", (object?)input.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", string.Join(", ", input.Tags));
        command.Parameters.AddWithValue("$item", result.ItemId);
        command.Parameters.AddWithValue("$duplicate", result.WasDuplicate ? 1 : 0);
        command.Parameters.AddWithValue("$undoDeadline", undoDeadlineAt.ToString("O"));
        command.Parameters.AddWithValue("$previousPrompt", (object?)previous?.Prompt ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousNotes", (object?)previous?.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousCategory", (object?)previous?.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousTags", (object?)previous?.Tags ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousDeletedAt", previous?.DeletedAt is { } deletedAt
            ? deletedAt.ToString("O")
            : DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CaptureSaveResult(captureId, result.ItemId, result.WasDuplicate, undoDeadlineAt);
    }

    public async Task<CaptureUndoResult> UndoCaptureAsync(
        Guid captureId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var pathsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long? itemId;
        bool wasDuplicate;

        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var capture = await ReadCaptureSessionAsync(
                connection,
                transaction,
                captureId,
                cancellationToken).ConfigureAwait(false);
            if (capture.UndoDeadlineAt is null || capture.UndoDeadlineAt < now)
            {
                throw new InvalidOperationException("撤销时间已结束。");
            }
            CaptureStateMachine.EnsureTransition(capture.State, CaptureState.Undone);
            itemId = capture.SavedItemId;
            wasDuplicate = capture.WasDuplicate;
            if (itemId is null)
            {
                throw new InvalidDataException("捕获记录缺少已保存项目。");
            }

            var guard = connection.CreateCommand();
            guard.Transaction = transaction;
            guard.CommandText = "SELECT fingerprint FROM capture_undo_guards WHERE capture_id = $capture;";
            guard.Parameters.AddWithValue("$capture", captureId.ToString("D"));
            var savedFingerprint = await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (savedFingerprint is null || !string.Equals(savedFingerprint,
                    await CaptureItemFingerprintAsync(connection, transaction, itemId.Value, cancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal))
                throw new InvalidOperationException("图片在收录后已有修改或无法核对原状态，已保留当前内容，不能撤销这次收录。");

            if (wasDuplicate)
            {
                await RestoreDuplicateSnapshotAsync(
                    connection,
                    transaction,
                    capture,
                    itemId.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var paths = connection.CreateCommand();
                paths.Transaction = transaction;
                paths.CommandText = """
                    SELECT a.original_path, a.thumbnail_path, a.medium_thumbnail_path
                    FROM image_assets a
                    JOIN collection_items ci ON ci.asset_id = a.id
                    WHERE ci.id = $item
                    LIMIT 1;
                    """;
                paths.Parameters.AddWithValue("$item", itemId.Value);
                await using (var reader = await paths.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        pathsToDelete.Add(reader.GetString(0));
                        pathsToDelete.Add(reader.GetString(1));
                        pathsToDelete.Add(reader.GetString(2));
                    }
                }

                var deleteItem = connection.CreateCommand();
                deleteItem.Transaction = transaction;
                deleteItem.CommandText = """
                    DELETE FROM image_assets
                    WHERE id = (SELECT asset_id FROM collection_items WHERE id = $item);
                    """;
                deleteItem.Parameters.AddWithValue("$item", itemId.Value);
                await deleteItem.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE capture_inbox
                SET state = $state,
                    undo_deadline_at = NULL,
                    error = NULL,
                    updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$state", (int)CaptureState.Undone);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", captureId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var fileResult = DeleteLibraryFiles(pathsToDelete);
        return new CaptureUndoResult(
            captureId,
            itemId,
            wasDuplicate,
            pathsToDelete.ToArray(),
            fileResult.Failures);
    }

    public async Task<CaptureSessionRecord> SetCaptureAiSummaryAsync(
        Guid captureId,
        string summary,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadCaptureStateAsync(
            connection,
            transaction,
            captureId,
            cancellationToken).ConfigureAwait(false);
        if (current == CaptureState.Saved)
        {
            CaptureStateMachine.EnsureTransition(current, CaptureState.WaitingForAi);
            current = CaptureState.WaitingForAi;
        }
        if (current != CaptureState.WaitingForAi)
        {
            throw new InvalidOperationException("只有已保存的捕获项目可以接收 AI 摘要。");
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE capture_inbox
            SET state = $state, ai_summary = $summary, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", (int)CaptureState.Saved);
        command.Parameters.AddWithValue("$summary", summary.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetCaptureSessionAsync(captureId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<CaptureSessionRecord> ClearCaptureAiSummaryAsync(
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capture_inbox
            SET ai_summary = NULL, updated_at = $now
            WHERE id = $id AND state IN ($saved, $waitingForAi);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        command.Parameters.AddWithValue("$saved", (int)CaptureState.Saved);
        command.Parameters.AddWithValue("$waitingForAi", (int)CaptureState.WaitingForAi);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("捕获项目已结束，不能清除 AI 摘要。");
        }
        return (await GetCaptureSessionAsync(captureId, cancellationToken).ConfigureAwait(false))!;
    }

    private static SqliteCommand BuildCaptureCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, state, staged_original_path, staged_small_path, staged_medium_path,
                   hash, extension, format, width, height, prompt, notes, category_id, tags,
                   captured_at, updated_at, prompt_deadline_at, saved_item_id, was_duplicate,
                   undo_deadline_at, error, last_clipboard_sequence, ai_summary
            FROM capture_inbox
            """;
        return command;
    }

    private static async Task<IReadOnlyList<CaptureSessionRecord>> ReadCaptureSessionsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var sessions = new List<CaptureSessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(ReadCaptureSession(reader));
        }
        return sessions;
    }

    private static CaptureSessionRecord ReadCaptureSession(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            (CaptureState)reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.GetString(13),
            DateTimeOffset.Parse(reader.GetString(14)),
            DateTimeOffset.Parse(reader.GetString(15)),
            DateTimeOffset.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.GetInt32(18) != 0,
            reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19)),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : checked((uint)reader.GetInt64(21)),
            reader.IsDBNull(22) ? null : reader.GetString(22));

    private static async Task<CaptureState> ReadCaptureStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM capture_inbox WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            throw new KeyNotFoundException($"找不到捕获项目 {captureId:D}。");
        }
        return (CaptureState)Convert.ToInt32(value);
    }

    private static async Task<CaptureSessionRecord> ReadCaptureSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        CancellationToken cancellationToken)
    {
        var command = BuildCaptureCommand(connection);
        command.Transaction = transaction;
        command.CommandText += " WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", captureId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"找不到捕获项目 {captureId:D}。");
        }
        return ReadCaptureSession(reader);
    }

    private static async Task<ExistingItemSnapshot?> ReadExistingItemSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hash,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ci.prompt,
                   ci.notes,
                   ci.category_id,
                   ci.deleted_at,
                   COALESCE((
                       SELECT group_concat(name, ', ')
                       FROM (
                           SELECT t.name AS name
                           FROM item_tags it
                           JOIN tags t ON t.id = it.tag_id
                           WHERE it.item_id = ci.id
                           ORDER BY t.name COLLATE NOCASE
                       )
                   ), '')
            FROM image_assets a
            JOIN collection_items ci ON ci.asset_id = a.id
            WHERE a.hash = $hash
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new ExistingItemSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(4),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)));
    }

    private async Task RestoreDuplicateSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureSessionRecord capture,
        long itemId,
        CancellationToken cancellationToken)
    {
        var previous = connection.CreateCommand();
        previous.Transaction = transaction;
        previous.CommandText = """
            SELECT previous_prompt, previous_notes, previous_category_id, previous_tags, previous_deleted_at
            FROM capture_inbox
            WHERE id = $id;
            """;
        previous.Parameters.AddWithValue("$id", capture.Id.ToString("D"));
        await using var reader = await previous.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1)
            || reader.IsDBNull(3))
        {
            throw new InvalidDataException("重复图片缺少撤销快照。");
        }
        var prompt = reader.GetString(0);
        var notes = reader.GetString(1);
        long? category = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        var tags = reader.GetString(3);
        var deletedAt = reader.IsDBNull(4) ? null : reader.GetString(4);
        await reader.DisposeAsync().ConfigureAwait(false);

        var restore = connection.CreateCommand();
        restore.Transaction = transaction;
        restore.CommandText = """
            UPDATE collection_items
            SET prompt = $prompt,
                notes = $notes,
                category_id = $category,
                deleted_at = $deletedAt,
                updated_at = $now
            WHERE id = $item;
            """;
        restore.Parameters.AddWithValue("$prompt", prompt);
        restore.Parameters.AddWithValue("$notes", notes);
        restore.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value);
        restore.Parameters.AddWithValue("$deletedAt", (object?)deletedAt ?? DBNull.Value);
        restore.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        restore.Parameters.AddWithValue("$item", itemId);
        await restore.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceTagsAsync(
            connection,
            transaction,
            itemId,
            ParseStoredTags(tags),
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ParseStoredTags(string tags) =>
        tags.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? NormalizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var normalized = error.Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private sealed record ExistingItemSnapshot(
        string Prompt,
        string Notes,
        long? CategoryId,
        string Tags,
        DateTimeOffset? DeletedAt);
}
