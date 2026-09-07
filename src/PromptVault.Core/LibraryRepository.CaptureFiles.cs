using Microsoft.Data.Sqlite;

namespace PromptVault.Core;

public sealed partial class LibraryRepository
{
    private async Task<AssetInput> PersistCaptureFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureSessionRecord capture,
        AssetInput asset,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(capture.Hash, asset.Hash, StringComparison.Ordinal))
            throw new InvalidDataException("收录图片与已准备的文件不一致。");
        var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT hash, original_path, thumbnail_path, medium_thumbnail_path, width, height, format
            FROM image_assets WHERE hash = $hash;
            """;
        query.Parameters.AddWithValue("$hash", asset.Hash);
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                asset = new AssetInput(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6));
        }

        // Resolve the current asset while holding the database write transaction.
        // Repair copies survive a later SQL failure; removing them could damage an existing item.
        foreach (var (source, destination) in new[]
                 {
                     (capture.StagedOriginalPath, asset.OriginalPath),
                     (capture.StagedSmallPath, asset.ThumbnailPath),
                     (capture.StagedMediumPath, asset.MediumThumbnailPath)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("收录暂存文件不完整。");
            var target = Paths.ToAbsolute(destination);
            if (File.Exists(target) && new FileInfo(target).Length > 0) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var input = new FileStream(Paths.ToAbsolute(source), FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (output.Length == 0) throw new InvalidDataException("收录暂存文件为空。");
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return asset;
    }
}
