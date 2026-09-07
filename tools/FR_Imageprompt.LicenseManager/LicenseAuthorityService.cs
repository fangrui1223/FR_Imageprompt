using System.IO;
using System.Security.Cryptography;
using PromptVault.Licensing;

namespace FR_Imageprompt.LicenseManager;

internal sealed class LicenseAuthorityService
{
    public bool HasPrivateKey => AuthorityCredentialStore.TryRead(out var key) && ClearAndReturnTrue(key);

    public void InitializeIfMissing()
    {
        if (AuthorityCredentialStore.TryRead(out var existing))
        {
            CryptographicOperations.ZeroMemory(existing);
            return;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        try { AuthorityCredentialStore.Save(privateKey); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    public byte[] ExportPublicKey()
    {
        var privateKey = ReadPrivateKey();
        try { return LicenseDocument.ExportPublicKey(privateKey); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    public bool PublicKeyMatchesProduct() => ExportPublicKey().AsSpan().SequenceEqual(ProductLicensePolicy.GetPublicKey());

    public string Issue(string licensee, string deviceCode, bool oneYear, DateTimeOffset nowUtc)
    {
        if (!PublicKeyMatchesProduct())
            throw new InvalidOperationException("当前签发私钥与 FR_Imageprompt V2 内置公钥不匹配，已阻止签发。请恢复正确的 V2 签发密钥备份。");
        licensee = licensee?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(licensee)) throw new ArgumentException("请填写使用人名称。");
        deviceCode = DeviceRequestCode.Normalize(deviceCode);
        if (!DeviceRequestCode.IsValid(deviceCode)) throw new ArgumentException("设备请求码格式不正确。");

        var payload = new LicensePayload(
            LicenseDocument.CurrentSchemaVersion,
            ProductLicensePolicy.Product,
            Guid.NewGuid(),
            licensee,
            deviceCode,
            nowUtc.ToUniversalTime(),
            oneYear ? nowUtc.ToUniversalTime().AddYears(1) : null,
            ProductLicensePolicy.LicensedMajorVersion,
            ProductLicensePolicy.LicensedMajorVersion,
            ProductLicensePolicy.KeyId);
        var privateKey = ReadPrivateKey();
        try { return LicenseDocument.Sign(payload, privateKey); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    public string ExportEncryptedBackup(string password)
    {
        var privateKey = ReadPrivateKey();
        try { return AuthorityBackup.Encrypt(privateKey, password, ProductLicensePolicy.KeyId); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    public void RestoreEncryptedBackup(string document, string password)
    {
        var privateKey = AuthorityBackup.Decrypt(document, password, ProductLicensePolicy.KeyId);
        try
        {
            var publicKey = LicenseDocument.ExportPublicKey(privateKey);
            if (!publicKey.AsSpan().SequenceEqual(ProductLicensePolicy.GetPublicKey()))
                throw new InvalidDataException("备份中的私钥不是 FR_Imageprompt V2 的签发密钥。");
            AuthorityCredentialStore.Save(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static byte[] ReadPrivateKey()
    {
        if (!AuthorityCredentialStore.TryRead(out var privateKey))
            throw new InvalidOperationException("未找到 V2 签发私钥。请从加密备份恢复；不要生成另一把密钥替代。");
        return privateKey;
    }

    private static bool ClearAndReturnTrue(byte[] bytes)
    {
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }
}
