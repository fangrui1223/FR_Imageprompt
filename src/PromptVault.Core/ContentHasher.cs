using System.Security.Cryptography;

namespace PromptVault.Core;

public static class ContentHasher
{
    public static async Task<string> Sha256Async(Stream stream, CancellationToken cancellationToken = default)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        return await Sha256Async(stream, cancellationToken).ConfigureAwait(false);
    }
}
