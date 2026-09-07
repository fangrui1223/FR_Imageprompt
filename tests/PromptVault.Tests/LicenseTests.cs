using System.Security.Cryptography;
using PromptVault.App.Services;
using PromptVault.Licensing;

namespace PromptVault.Tests;

public sealed class LicenseTests
{
    private const string KeyId = "test-v2-key";
    private const string Product = "FR_Imageprompt";
    private static readonly DateTimeOffset Issued = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly string Device = DeviceRequestCode.Create("machine-a", 0x1234ABCD);

    [Fact]
    public void DeviceRequestCodeIsDeterministicNormalizedAndAnonymous()
    {
        var first = DeviceRequestCode.Create("  abcd-1234  ", 0x01020304);
        var second = DeviceRequestCode.Create("ABCD-1234", 0x01020304);
        var changed = DeviceRequestCode.Create("ABCD-1234", 0x01020305);

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
        Assert.True(DeviceRequestCode.IsValid(first));
        Assert.StartsWith("FR2-", first);
        Assert.DoesNotContain("ABCD", first);
    }

    [Fact]
    public void PermanentLicenseCoversEveryV2MinorButNotV3()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = Sign(key, CreatePayload());
        var publicKey = key.ExportSubjectPublicKeyInfo();

        Assert.True(Validate(document, publicKey, Device, 2, Issued.AddYears(20)).IsValid);
        Assert.True(Validate(document, publicKey, Device, 2, Issued.AddYears(100)).IsValid);
        Assert.Equal(LicenseValidationStatus.VersionNotCovered, Validate(document, publicKey, Device, 3, Issued).Status);
    }

    [Fact]
    public void OneYearLicenseExpiresAtSignedBoundary()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expiry = Issued.AddYears(1);
        var document = Sign(key, CreatePayload(expiry));
        var publicKey = key.ExportSubjectPublicKeyInfo();

        Assert.True(Validate(document, publicKey, Device, 2, expiry.AddTicks(-1)).IsValid);
        Assert.Equal(LicenseValidationStatus.Expired, Validate(document, publicKey, Device, 2, expiry).Status);
    }

    [Fact]
    public void WrongDeviceProductKeyAndFutureIssueAreRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var document = Sign(key, CreatePayload());

        Assert.Equal(
            LicenseValidationStatus.WrongDevice,
            Validate(document, publicKey, DeviceRequestCode.Create("machine-b", 44), 2, Issued).Status);
        var wrongProduct = Sign(key, CreatePayload() with { Product = "Other" });
        Assert.Equal(LicenseValidationStatus.WrongProduct, Validate(wrongProduct, publicKey, Device, 2, Issued).Status);
        Assert.Equal(
            LicenseValidationStatus.UnknownKey,
            LicenseDocument.Validate(document, "another-key", publicKey, Product, Device, 2, Issued).Status);
        Assert.Equal(
            LicenseValidationStatus.NotYetValid,
            Validate(document, publicKey, Device, 2, Issued.AddMinutes(-6)).Status);
    }

    [Fact]
    public void SignatureTamperingAndMalformedDocumentsNeverValidate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = Sign(key, CreatePayload());
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var signatureMarker = "\"signature\": \"";
        var position = document.IndexOf(signatureMarker, StringComparison.Ordinal) + signatureMarker.Length;
        var replacement = document[position] == 'A' ? 'B' : 'A';
        var tampered = document[..position] + replacement + document[(position + 1)..];

        Assert.Equal(LicenseValidationStatus.InvalidSignature, Validate(tampered, publicKey, Device, 2, Issued).Status);
        Assert.Equal(LicenseValidationStatus.Malformed, Validate("{}", publicKey, Device, 2, Issued).Status);
        Assert.Equal(LicenseValidationStatus.Malformed, Validate("{ not json", publicKey, Device, 2, Issued).Status);
        Assert.Equal(
            LicenseValidationStatus.Malformed,
            Validate(new string('x', LicenseDocument.MaximumDocumentBytes + 1), publicKey, Device, 2, Issued).Status);
    }

    [Fact]
    public void AuthorityBackupRoundTripsAndRejectsWrongPasswordOrTampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        try
        {
            var backup = AuthorityBackup.Encrypt(privateKey, "correct horse battery", KeyId);
            var restored = AuthorityBackup.Decrypt(backup, "correct horse battery", KeyId);
            try { Assert.Equal(privateKey, restored); }
            finally { CryptographicOperations.ZeroMemory(restored); }

            Assert.Throws<InvalidDataException>(() => AuthorityBackup.Decrypt(backup, "wrong password value", KeyId));
            var tampered = backup.Replace("\"ciphertext\": \"", "\"ciphertext\": \"A", StringComparison.Ordinal);
            Assert.ThrowsAny<Exception>(() => AuthorityBackup.Decrypt(tampered, "correct horse battery", KeyId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    [Fact]
    public void ProductLicenseImportIsValidatedAndAtomic()
    {
        var root = Path.Combine(Path.GetTempPath(), "FRImagepromptLicenseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = key.ExportSubjectPublicKeyInfo();
            var validDocument = Sign(key, CreatePayload() with { IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
            var source = Path.Combine(root, "source.frlicense");
            var storage = Path.Combine(root, "local", "license.frlicense");
            File.WriteAllText(source, validDocument);
            var service = new ProductLicenseService(Device, 2, publicKey, KeyId, Product, storage, usesExplicitPath: false);

            var imported = service.Import(source);

            Assert.True(imported.IsValid);
            Assert.True(service.ValidateCurrent().IsValid);
            Assert.Equal(validDocument, File.ReadAllText(storage));

            File.WriteAllText(source, "corrupt");
            Assert.False(service.Import(source).IsValid);
            Assert.Equal(validDocument, File.ReadAllText(storage));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(storage)!, "*.tmp-*"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static LicensePayload CreatePayload(DateTimeOffset? expiry = null) => new(
        LicenseDocument.CurrentSchemaVersion,
        Product,
        Guid.Parse("6b7393a1-6b6b-48dd-b447-97d84685f5ce"),
        "Synthetic User",
        Device,
        Issued,
        expiry,
        2,
        2,
        KeyId);

    private static string Sign(ECDsa key, LicensePayload payload)
    {
        var privateKey = key.ExportPkcs8PrivateKey();
        try { return LicenseDocument.Sign(payload, privateKey); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    private static LicenseValidationResult Validate(
        string document,
        byte[] publicKey,
        string device,
        int major,
        DateTimeOffset now) => LicenseDocument.Validate(document, KeyId, publicKey, Product, device, major, now);
}
