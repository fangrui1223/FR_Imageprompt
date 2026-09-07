using System.Reflection;

namespace PromptVault.Licensing;

public static class ProductLicensePolicy
{
    public const string Product = "FR_Imageprompt";
    public const string KeyId = "fr-imageprompt-v2-2026-01";
    public const int LicensedMajorVersion = 2;

    // The matching private key is stored only in the owner's Windows Credential Manager.
    public const string PublicKeySubjectPublicKeyInfoBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9rF3NExojGyM5TzFf0nzpaRNFVaxyXvMRKM2ky3+ehGVHAmGcmkVyQZfm0uvo+Vb3Q1ytmyJQ0kaQnDFvQyfTA==";

    public static byte[] GetPublicKey() => Convert.FromBase64String(PublicKeySubjectPublicKeyInfoBase64);

    public static int GetCurrentMajorVersion(Assembly? assembly = null) =>
        (assembly ?? Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version?.Major
        ?? LicensedMajorVersion;
}
