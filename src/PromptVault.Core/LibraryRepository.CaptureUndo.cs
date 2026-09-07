using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    private async Task<string> CaptureItemFingerprintAsync(
        SqliteConnection connection, SqliteTransaction transaction, long itemId, CancellationToken cancellationToken)
    {
        // Include fields edited outside collection_items (tags and confirmed metadata),
        // plus board references so undoing a new capture cannot invalidate later board work.
        var tables = new List<List<object?[]>>();
        foreach (var sql in new[]
                 {
                     """
                     SELECT ci.id, ci.asset_id, ci.prompt, ci.notes, ci.category_id, ci.created_at,
                            ci.updated_at, ci.deleted_at, ci.is_favorite, a.original_path
                     FROM collection_items ci JOIN image_assets a ON a.id = ci.asset_id WHERE ci.id = $item;
                     """,
                     "SELECT t.id, t.name FROM item_tags it JOIN tags t ON t.id = it.tag_id WHERE it.item_id = $item ORDER BY t.id;",
                     "SELECT field_type, value, source_candidate_id, updated_at FROM user_metadata WHERE item_id = $item ORDER BY field_type;",
                     "SELECT id, board_id FROM board_items WHERE asset_id = (SELECT asset_id FROM collection_items WHERE id = $item) ORDER BY id;"
                 })
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$item", itemId);
            var rows = new List<object?[]>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
            tables.Add(rows);
        }
        string? originalHash = null;
        if (tables[0].FirstOrDefault() is { } item && item[9] is string relative)
        {
            var path = Paths.ToAbsolute(relative);
            if (File.Exists(path))
            {
                await using var original = File.OpenRead(path);
                originalHash = await ContentHasher.Sha256Async(original, cancellationToken).ConfigureAwait(false);
            }
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { tables, originalHash })));
    }
}
