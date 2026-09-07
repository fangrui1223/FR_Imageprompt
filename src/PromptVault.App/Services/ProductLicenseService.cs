using System.Text;
using PromptVault.Licensing;

namespace PromptVault.App.Services;

internal sealed class ProductLicenseService
{
    private readonly string _deviceCode;
    private readonly int _currentMajorVersion;
    private readonly byte[] _publicKey;
    private readonly string _expectedKeyId;
    private readonly string _expectedProduct;
    private readonly string _storagePath;

    public ProductLicenseService(string? explicitLicensePath = null)
        : this(
            WindowsDeviceIdentity.GetRequestCode(),
            ProductLicensePolicy.GetCurrentMajorVersion(typeof(App).Assembly),
            ProductLicensePolicy.GetPublicKey(),
            ProductLicensePolicy.KeyId,
            ProductLicensePolicy.Product,
            string.IsNullOrWhiteSpace(explicitLicensePath) ? DefaultLicensePath : Path.GetFullPath(explicitLicensePath),
            !string.IsNullOrWhiteSpace(explicitLicensePath))
    {
    }

    internal ProductLicenseService(
        string deviceCode,
        int currentMajorVersion,
        byte[] publicKey,
        string expectedKeyId,
        string expectedProduct,
        string storagePath,
        bool usesExplicitPath)
    {
        _deviceCode = deviceCode;
        _currentMajorVersion = currentMajorVersion;
        _publicKey = publicKey;
        _expectedKeyId = expectedKeyId;
        _expectedProduct = expectedProduct;
        _storagePath = Path.GetFullPath(storagePath);
        LicensePath = _storagePath;
        UsesExplicitPath = usesExplicitPath;
    }

    public string DeviceCode => _deviceCode;
    public string LicensePath { get; }
    public bool UsesExplicitPath { get; }

    internal static string DefaultLicensePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FR_Imageprompt",
        "license.frlicense");

    public LicenseValidationResult ValidateCurrent()
    {
        if (!File.Exists(LicensePath))
            return new LicenseValidationResult(LicenseValidationStatus.Missing, "这台电脑尚未导入许可证。");
        try
        {
            var info = new FileInfo(LicensePath);
            if (info.Length > LicenseDocument.MaximumDocumentBytes)
                return new LicenseValidationResult(LicenseValidationStatus.Malformed, "许可证文件过大或格式不正确。");
            return ValidateDocument(File.ReadAllText(LicensePath, Encoding.UTF8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LicenseValidationResult(LicenseValidationStatus.Unreadable, "许可证文件无法读取，请重新导入。", null);
        }
    }

    public LicenseValidationResult ValidateDocument(string document) => LicenseDocument.Validate(
        document,
        _expectedKeyId,
        _publicKey,
        _expectedProduct,
        _deviceCode,
        _currentMajorVersion,
        DateTimeOffset.UtcNow);

    public LicenseValidationResult Import(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (UsesExplicitPath)
            return new LicenseValidationResult(
                LicenseValidationStatus.Unreadable,
                "显式 --license 验证模式不会修改许可证文件，请移除该参数后再导入。");

        string document;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) return new LicenseValidationResult(LicenseValidationStatus.Missing, "选择的许可证文件不存在。");
            if (info.Length > LicenseDocument.MaximumDocumentBytes)
                return new LicenseValidationResult(LicenseValidationStatus.Malformed, "许可证文件过大或格式不正确。");
            document = File.ReadAllText(sourcePath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LicenseValidationResult(LicenseValidationStatus.Unreadable, "无法读取选择的许可证文件。");
        }

        var validation = ValidateDocument(document);
        if (!validation.IsValid) return validation;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            var temporary = _storagePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, document, new UTF8Encoding(false));
                File.Move(temporary, _storagePath, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
            return validation;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LicenseValidationResult(LicenseValidationStatus.Unreadable, "许可证有效，但无法保存到本机。请检查当前 Windows 账户的文件权限。");
        }
    }
}
