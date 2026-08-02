using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    public async Task<IReadOnlyList<BoardRecord>> GetBoardsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, background_style, view_offset_x, view_offset_y, zoom,
                   created_at, updated_at
            FROM boards
            ORDER BY updated_at DESC, id DESC;
            """;
        var boards = new List<BoardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            boards.Add(ReadBoard(reader));
        }
        return boards;
    }

    public async Task<BoardRecord> CreateBoardAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeBoardName(name);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO boards(
                name, background_style, view_offset_x, view_offset_y, zoom,
                created_at, updated_at)
            VALUES($name, 'neutral', 0, 0, 1, $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try
        {
            var id = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            return new BoardRecord(id, name, "neutral", 0, 0, 1, now, now);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"画板名称“{name}”已存在。", ex);
        }
    }

    public async Task RenameBoardAsync(
        long boardId,
        string name,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeBoardName(name);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE boards SET name = $name, updated_at = $now WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", boardId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new KeyNotFoundException($"画板 {boardId} 不存在。");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"画板名称“{name}”已存在。", ex);
        }
    }

    public async Task DeleteBoardAsync(
        long boardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM boards WHERE id = $id;";
        command.Parameters.AddWithValue("$id", boardId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateBoardViewAsync(
        long boardId,
        double offsetX,
        double offsetY,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            throw new ArgumentOutOfRangeException(nameof(offsetX));
        zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1, 0.1, 8);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE boards
            SET view_offset_x = $x, view_offset_y = $y, zoom = $zoom, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", boardId);
        command.Parameters.AddWithValue("$x", offsetX);
        command.Parameters.AddWithValue("$y", offsetY);
        command.Parameters.AddWithValue("$zoom", zoom);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateBoardBackgroundAsync(
        long boardId,
        string backgroundStyle,
        CancellationToken cancellationToken = default)
    {
        backgroundStyle = string.IsNullOrWhiteSpace(backgroundStyle)
            ? "neutral"
            : backgroundStyle.Trim();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE boards SET background_style = $style, updated_at = $now WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", boardId);
        command.Parameters.AddWithValue("$style", backgroundStyle);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BoardDocument?> GetBoardDocumentAsync(
        long boardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var boardCommand = connection.CreateCommand();
        boardCommand.CommandText = """
            SELECT id, name, background_style, view_offset_x, view_offset_y, zoom,
                   created_at, updated_at
            FROM boards WHERE id = $id LIMIT 1;
            """;
        boardCommand.Parameters.AddWithValue("$id", boardId);
        BoardRecord? board;
        await using (var reader = await boardCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            board = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadBoard(reader)
                : null;
        }
        if (board is null) return null;

        var groups = await GetBoardGroupsAsync(
            connection,
            boardId,
            cancellationToken).ConfigureAwait(false);
        var items = await GetBoardItemsAsync(
            connection,
            boardId,
            cancellationToken).ConfigureAwait(false);
        var notes = await GetBoardNotesAsync(
            connection,
            boardId,
            cancellationToken).ConfigureAwait(false);
        return new BoardDocument(board, groups, items, notes);
    }

    public async Task<IReadOnlyList<BoardItemRecord>> AddBoardItemsAsync(
        long boardId,
        IReadOnlyList<BoardItemPlacementInput> placements,
        CancellationToken cancellationToken = default)
    {
        if (placements.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBoardExistsAsync(connection, transaction, boardId, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var placement in placements)
        {
            ValidatePlacement(placement);
            if (placement.GroupId is { } groupId)
            {
                await EnsureGroupBelongsToBoardAsync(
                    connection,
                    transaction,
                    boardId,
                    groupId,
                    cancellationToken).ConfigureAwait(false);
            }

            var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = """
                SELECT ci.asset_id, a.original_path
                FROM collection_items ci
                JOIN image_assets a ON a.id = ci.asset_id
                WHERE ci.id = $item
                LIMIT 1;
                """;
            source.Parameters.AddWithValue("$item", placement.CollectionItemId);
            long assetId;
            string sourcePath;
            await using (var reader = await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new KeyNotFoundException($"图库图片 {placement.CollectionItemId} 不存在。");
                assetId = reader.GetInt64(0);
                sourcePath = reader.GetString(1);
            }

            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO board_items(
                    board_id, asset_id, source_path_snapshot, source_path_override,
                    x, y, width, height, z_index, rotation,
                    crop_left, crop_top, crop_right, crop_bottom, group_id,
                    created_at, updated_at)
                VALUES(
                    $board, $asset, $source, NULL,
                    $x, $y, $width, $height, $z, $rotation,
                    $cropLeft, $cropTop, $cropRight, $cropBottom, $group,
                    $now, $now);
                """;
            AddPlacementParameters(insert, boardId, assetId, sourcePath, placement, now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetBoardItemsAsync(boardId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BoardItemRecord>> GetBoardItemsAsync(
        long boardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetBoardItemsAsync(connection, boardId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateBoardItemAsync(
        long boardId,
        BoardItemUpdate update,
        CancellationToken cancellationToken = default)
    {
        ValidateItemUpdate(update);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (update.GroupId is { } groupId)
        {
            await EnsureGroupBelongsToBoardAsync(
                connection,
                transaction,
                boardId,
                groupId,
                cancellationToken).ConfigureAwait(false);
        }
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE board_items
            SET x = $x, y = $y, width = $width, height = $height,
                z_index = $z, rotation = $rotation,
                crop_left = $cropLeft, crop_top = $cropTop,
                crop_right = $cropRight, crop_bottom = $cropBottom,
                group_id = $group, source_path_override = $override,
                updated_at = $now
            WHERE id = $id AND board_id = $board;
            """;
        AddItemUpdateParameters(command, boardId, update, DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"画板项 {update.Id} 不存在。");
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateBoardItemsAsync(
        long boardId,
        IReadOnlyList<BoardItemUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0) return;
        if (updates.Select(update => update.Id).Distinct().Count() != updates.Count)
            throw new ArgumentException("批量画板更新不能包含重复项目。", nameof(updates));
        foreach (var update in updates) ValidateItemUpdate(update);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var update in updates)
        {
            if (update.GroupId is { } groupId)
            {
                await EnsureGroupBelongsToBoardAsync(
                    connection, transaction, boardId, groupId, cancellationToken).ConfigureAwait(false);
            }
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE board_items
                SET x = $x, y = $y, width = $width, height = $height,
                    z_index = $z, rotation = $rotation,
                    crop_left = $cropLeft, crop_top = $cropTop,
                    crop_right = $cropRight, crop_bottom = $cropBottom,
                    group_id = $group, source_path_override = $override,
                    updated_at = $now
                WHERE id = $id AND board_id = $board;
                """;
            AddItemUpdateParameters(command, boardId, update, now);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new KeyNotFoundException($"画板项 {update.Id} 不存在。");
        }
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBoardItemsAsync(
        long boardId,
        IReadOnlyCollection<long> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var itemId in itemIds.Distinct())
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM board_items WHERE board_id = $board AND id = $id;";
            command.Parameters.AddWithValue("$board", boardId);
            command.Parameters.AddWithValue("$id", itemId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceBoardItemsAsync(
        long boardId,
        IReadOnlyList<BoardItemRecord> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Any(item => item.BoardId != boardId))
            throw new InvalidOperationException("画板快照包含其他画板的项目。");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBoardExistsAsync(connection, transaction, boardId, cancellationToken)
            .ConfigureAwait(false);
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM board_items WHERE board_id = $board;";
        delete.Parameters.AddWithValue("$board", boardId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO board_items(
                    id, board_id, asset_id, source_path_snapshot, source_path_override,
                    x, y, width, height, z_index, rotation,
                    crop_left, crop_top, crop_right, crop_bottom, group_id,
                    created_at, updated_at)
                VALUES(
                    $id, $board, $asset, $source, $override,
                    $x, $y, $width, $height, $z, $rotation,
                    $cropLeft, $cropTop, $cropRight, $cropBottom, $group,
                    $created, $updated);
                """;
            insert.Parameters.AddWithValue("$id", item.Id);
            insert.Parameters.AddWithValue("$board", boardId);
            insert.Parameters.AddWithValue("$asset", (object?)item.AssetId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$source", item.SourcePathSnapshot);
            insert.Parameters.AddWithValue("$override", (object?)item.SourcePathOverride ?? DBNull.Value);
            insert.Parameters.AddWithValue("$x", item.X);
            insert.Parameters.AddWithValue("$y", item.Y);
            insert.Parameters.AddWithValue("$width", item.Width);
            insert.Parameters.AddWithValue("$height", item.Height);
            insert.Parameters.AddWithValue("$z", item.ZIndex);
            insert.Parameters.AddWithValue("$rotation", item.Rotation);
            insert.Parameters.AddWithValue("$cropLeft", item.CropLeft);
            insert.Parameters.AddWithValue("$cropTop", item.CropTop);
            insert.Parameters.AddWithValue("$cropRight", item.CropRight);
            insert.Parameters.AddWithValue("$cropBottom", item.CropBottom);
            insert.Parameters.AddWithValue("$group", (object?)item.GroupId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
            insert.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BoardGroupRecord> CreateBoardGroupAsync(
        long boardId,
        string name,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeGroupName(name);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBoardExistsAsync(connection, transaction, boardId, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO board_groups(board_id, name, sort_order, created_at, updated_at)
            VALUES(
                $board, $name,
                COALESCE((SELECT MAX(sort_order) + 1 FROM board_groups WHERE board_id = $board), 0),
                $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var groups = await GetBoardGroupsAsync(boardId, cancellationToken).ConfigureAwait(false);
        return groups.Single(group => group.Id == id);
    }

    public async Task<IReadOnlyList<BoardGroupRecord>> GetBoardGroupsAsync(
        long boardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetBoardGroupsAsync(connection, boardId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameBoardGroupAsync(
        long boardId,
        long groupId,
        string name,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeGroupName(name);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE board_groups
            SET name = $name, updated_at = $now
            WHERE id = $id AND board_id = $board;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$id", groupId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"画板分组 {groupId} 不存在。");
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBoardGroupAsync(
        long boardId,
        long groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = """
            UPDATE board_items SET group_id = NULL, updated_at = $now
            WHERE board_id = $board AND group_id = $group;
            """;
        clear.Parameters.AddWithValue("$board", boardId);
        clear.Parameters.AddWithValue("$group", groupId);
        clear.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM board_groups WHERE board_id = $board AND id = $group;";
        delete.Parameters.AddWithValue("$board", boardId);
        delete.Parameters.AddWithValue("$group", groupId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetBoardItemsGroupAsync(
        long boardId,
        IReadOnlyCollection<long> itemIds,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (groupId is { } value)
        {
            await EnsureGroupBelongsToBoardAsync(
                connection,
                transaction,
                boardId,
                value,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var itemId in itemIds.Distinct())
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE board_items SET group_id = $group, updated_at = $now
                WHERE board_id = $board AND id = $id;
                """;
            command.Parameters.AddWithValue("$board", boardId);
            command.Parameters.AddWithValue("$id", itemId);
            command.Parameters.AddWithValue("$group", (object?)groupId ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BoardNoteRecord> AddBoardNoteAsync(
        long boardId,
        string text,
        double x,
        double y,
        double width = 300,
        double height = 220,
        int zIndex = 0,
        string colorStyle = "yellow",
        CancellationToken cancellationToken = default)
    {
        var update = new BoardNoteUpdate(
            0,
            NormalizeNoteText(text),
            x,
            y,
            width,
            height,
            zIndex,
            NormalizeNoteColor(colorStyle));
        ValidateNoteUpdate(update);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBoardExistsAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO board_notes(
                board_id, text, x, y, width, height, z_index, color_style,
                created_at, updated_at)
            VALUES(
                $board, $text, $x, $y, $width, $height, $z, $color,
                $now, $now);
            SELECT last_insert_rowid();
            """;
        AddNoteParameters(command, boardId, update, now.ToString("O"), includeId: false);
        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BoardNoteRecord(
            id,
            boardId,
            update.Text,
            update.X,
            update.Y,
            update.Width,
            update.Height,
            update.ZIndex,
            update.ColorStyle,
            now,
            now);
    }

    public async Task<IReadOnlyList<BoardNoteRecord>> GetBoardNotesAsync(
        long boardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetBoardNotesAsync(connection, boardId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateBoardNoteAsync(
        long boardId,
        BoardNoteUpdate update,
        CancellationToken cancellationToken = default)
    {
        update = update with
        {
            Text = NormalizeNoteText(update.Text),
            ColorStyle = NormalizeNoteColor(update.ColorStyle)
        };
        ValidateNoteUpdate(update);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE board_notes
            SET text = $text, x = $x, y = $y, width = $width, height = $height,
                z_index = $z, color_style = $color, updated_at = $now
            WHERE id = $id AND board_id = $board;
            """;
        AddNoteParameters(
            command,
            boardId,
            update,
            DateTimeOffset.UtcNow.ToString("O"),
            includeId: true);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"画板便签 {update.Id} 不存在。");
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBoardNotesAsync(
        long boardId,
        IReadOnlyCollection<long> noteIds,
        CancellationToken cancellationToken = default)
    {
        if (noteIds.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var noteId in noteIds.Distinct())
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM board_notes WHERE board_id = $board AND id = $id;";
            command.Parameters.AddWithValue("$board", boardId);
            command.Parameters.AddWithValue("$id", noteId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await TouchBoardAsync(connection, transaction, boardId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<BoardGroupRecord>> GetBoardGroupsAsync(
        SqliteConnection connection,
        long boardId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, board_id, name, sort_order, created_at, updated_at
            FROM board_groups
            WHERE board_id = $board
            ORDER BY sort_order, id;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        var groups = new List<BoardGroupRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(new BoardGroupRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5))));
        }
        return groups;
    }

    private static async Task<IReadOnlyList<BoardItemRecord>> GetBoardItemsAsync(
        SqliteConnection connection,
        long boardId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT bi.id, bi.board_id, bi.asset_id, bi.source_path_snapshot,
                   bi.source_path_override,
                   a.original_path, a.thumbnail_path, a.medium_thumbnail_path,
                   COALESCE(a.width, 0), COALESCE(a.height, 0), COALESCE(a.format, ''),
                   bi.x, bi.y, bi.width, bi.height, bi.z_index, bi.rotation,
                   bi.crop_left, bi.crop_top, bi.crop_right, bi.crop_bottom,
                   bi.group_id, bi.created_at, bi.updated_at
            FROM board_items bi
            LEFT JOIN image_assets a ON a.id = bi.asset_id
            WHERE bi.board_id = $board
            ORDER BY bi.z_index, bi.id;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        var items = new List<BoardItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new BoardItemRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.GetDouble(11),
                reader.GetDouble(12),
                reader.GetDouble(13),
                reader.GetDouble(14),
                reader.GetInt32(15),
                reader.GetDouble(16),
                reader.GetDouble(17),
                reader.GetDouble(18),
                reader.GetDouble(19),
                reader.GetDouble(20),
                reader.IsDBNull(21) ? null : reader.GetInt64(21),
                DateTimeOffset.Parse(reader.GetString(22)),
                DateTimeOffset.Parse(reader.GetString(23))));
        }
        return items;
    }

    private static async Task<IReadOnlyList<BoardNoteRecord>> GetBoardNotesAsync(
        SqliteConnection connection,
        long boardId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, board_id, text, x, y, width, height, z_index, color_style,
                   created_at, updated_at
            FROM board_notes
            WHERE board_id = $board
            ORDER BY z_index, id;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        var notes = new List<BoardNoteRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            notes.Add(new BoardNoteRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetInt32(7),
                reader.GetString(8),
                DateTimeOffset.Parse(reader.GetString(9)),
                DateTimeOffset.Parse(reader.GetString(10))));
        }
        return notes;
    }

    private static BoardRecord ReadBoard(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetDouble(3),
        reader.GetDouble(4),
        reader.GetDouble(5),
        DateTimeOffset.Parse(reader.GetString(6)),
        DateTimeOffset.Parse(reader.GetString(7)));

    private static string NormalizeBoardName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("画板名称不能为空。", nameof(name));
        if (name.Length > 80) throw new ArgumentException("画板名称不能超过 80 个字符。", nameof(name));
        return name;
    }

    private static string NormalizeGroupName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("分组名称不能为空。", nameof(name));
        if (name.Length > 80) throw new ArgumentException("分组名称不能超过 80 个字符。", nameof(name));
        return name;
    }

    private static string NormalizeNoteText(string text)
    {
        text ??= "";
        return text.Length <= 4_000
            ? text
            : throw new ArgumentException("画板便签不能超过 4000 个字符。", nameof(text));
    }

    private static string NormalizeNoteColor(string colorStyle)
    {
        colorStyle = string.IsNullOrWhiteSpace(colorStyle)
            ? "yellow"
            : colorStyle.Trim().ToLowerInvariant();
        return colorStyle is "yellow" or "rose" or "blue" or "slate"
            ? colorStyle
            : throw new ArgumentException("不支持的画板便签颜色。", nameof(colorStyle));
    }

    private static void ValidatePlacement(BoardItemPlacementInput placement)
    {
        if (!double.IsFinite(placement.X) || !double.IsFinite(placement.Y))
            throw new ArgumentOutOfRangeException(nameof(placement));
        if (!double.IsFinite(placement.Width) || !double.IsFinite(placement.Height)
            || placement.Width <= 0 || placement.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(placement));
        ValidateCrop(
            placement.CropLeft,
            placement.CropTop,
            placement.CropRight,
            placement.CropBottom);
    }

    private static void ValidateItemUpdate(BoardItemUpdate update)
    {
        if (!double.IsFinite(update.X) || !double.IsFinite(update.Y)
            || !double.IsFinite(update.Width) || !double.IsFinite(update.Height)
            || update.Width <= 0 || update.Height <= 0
            || !double.IsFinite(update.Rotation))
            throw new ArgumentOutOfRangeException(nameof(update));
        ValidateCrop(update.CropLeft, update.CropTop, update.CropRight, update.CropBottom);
    }

    private static void ValidateNoteUpdate(BoardNoteUpdate update)
    {
        if (!double.IsFinite(update.X) || !double.IsFinite(update.Y)
            || !double.IsFinite(update.Width) || !double.IsFinite(update.Height)
            || update.Width < 120 || update.Height < 100)
            throw new ArgumentOutOfRangeException(nameof(update));
    }

    private static void ValidateCrop(double left, double top, double right, double bottom)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)
            || !double.IsFinite(right) || !double.IsFinite(bottom)
            || left < 0 || top < 0 || right < 0 || bottom < 0
            || left > 0.95 || top > 0.95 || right > 0.95 || bottom > 0.95
            || left + right >= 1 || top + bottom >= 1)
            throw new ArgumentOutOfRangeException(nameof(left), "裁剪比例必须保留可见区域。");
    }

    private static void AddPlacementParameters(
        SqliteCommand command,
        long boardId,
        long assetId,
        string sourcePath,
        BoardItemPlacementInput placement,
        string now)
    {
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$asset", assetId);
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$x", placement.X);
        command.Parameters.AddWithValue("$y", placement.Y);
        command.Parameters.AddWithValue("$width", placement.Width);
        command.Parameters.AddWithValue("$height", placement.Height);
        command.Parameters.AddWithValue("$z", placement.ZIndex);
        command.Parameters.AddWithValue("$rotation", placement.Rotation);
        command.Parameters.AddWithValue("$cropLeft", placement.CropLeft);
        command.Parameters.AddWithValue("$cropTop", placement.CropTop);
        command.Parameters.AddWithValue("$cropRight", placement.CropRight);
        command.Parameters.AddWithValue("$cropBottom", placement.CropBottom);
        command.Parameters.AddWithValue("$group", (object?)placement.GroupId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
    }

    private static void AddItemUpdateParameters(
        SqliteCommand command,
        long boardId,
        BoardItemUpdate update,
        string now)
    {
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$id", update.Id);
        command.Parameters.AddWithValue("$x", update.X);
        command.Parameters.AddWithValue("$y", update.Y);
        command.Parameters.AddWithValue("$width", update.Width);
        command.Parameters.AddWithValue("$height", update.Height);
        command.Parameters.AddWithValue("$z", update.ZIndex);
        command.Parameters.AddWithValue("$rotation", update.Rotation);
        command.Parameters.AddWithValue("$cropLeft", update.CropLeft);
        command.Parameters.AddWithValue("$cropTop", update.CropTop);
        command.Parameters.AddWithValue("$cropRight", update.CropRight);
        command.Parameters.AddWithValue("$cropBottom", update.CropBottom);
        command.Parameters.AddWithValue("$group", (object?)update.GroupId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$override",
            string.IsNullOrWhiteSpace(update.SourcePathOverride)
                ? DBNull.Value
                : update.SourcePathOverride.Trim());
        command.Parameters.AddWithValue("$now", now);
    }

    private static void AddNoteParameters(
        SqliteCommand command,
        long boardId,
        BoardNoteUpdate update,
        string now,
        bool includeId)
    {
        command.Parameters.AddWithValue("$board", boardId);
        if (includeId) command.Parameters.AddWithValue("$id", update.Id);
        command.Parameters.AddWithValue("$text", update.Text);
        command.Parameters.AddWithValue("$x", update.X);
        command.Parameters.AddWithValue("$y", update.Y);
        command.Parameters.AddWithValue("$width", update.Width);
        command.Parameters.AddWithValue("$height", update.Height);
        command.Parameters.AddWithValue("$z", update.ZIndex);
        command.Parameters.AddWithValue("$color", update.ColorStyle);
        command.Parameters.AddWithValue("$now", now);
    }

    private static async Task EnsureBoardExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long boardId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM boards WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", boardId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new KeyNotFoundException($"画板 {boardId} 不存在。");
    }

    private static async Task EnsureGroupBelongsToBoardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long boardId,
        long groupId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM board_groups WHERE id = $group AND board_id = $board LIMIT 1;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$group", groupId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("不能把画板项分配到其他画板的分组。");
    }

    private static async Task TouchBoardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long boardId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE boards SET updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$id", boardId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
