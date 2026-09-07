using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptVault.Licensing;

public static class AuthorityBackup
{
    private const int Iterations = 210_000;

    public static string Encrypt(ReadOnlySpan<byte> privateKeyPkcs8, string password, string keyId)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var ciphertext = new byte[privateKeyPkcs8.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, privateKeyPkcs8, ciphertext, tag, Encoding.UTF8.GetBytes(keyId));
            return JsonSerializer.Serialize(new BackupEnvelope(
                1,
                keyId,
                Iterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag)), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Decrypt(string document, string password, string expectedKeyId)
    {
        ValidatePassword(password);
        if (Encoding.UTF8.GetByteCount(document) > 16 * 1024) throw new InvalidDataException("签发密钥备份文件过大。");
        var envelope = JsonSerializer.Deserialize<BackupEnvelope>(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException("签发密钥备份为空。");
        if (envelope.SchemaVersion != 1 || envelope.KeyId != expectedKeyId || envelope.Iterations != Iterations)
            throw new InvalidDataException("签发密钥备份版本或密钥标识不匹配。");
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length is < 80 or > 1024)
            throw new InvalidDataException("签发密钥备份结构无效。");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(expectedKeyId));
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("备份口令错误，或备份文件已被修改。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            throw new ArgumentException("备份口令至少需要 10 个字符。", nameof(password));
    }

    private sealed record BackupEnvelope(
        int SchemaVersion,
        string KeyId,
        int Iterations,
        string Salt,
        string Nonce,
        string Ciphertext,
        string Tag);
}
