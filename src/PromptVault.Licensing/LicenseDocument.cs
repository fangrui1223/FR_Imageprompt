using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptVault.Licensing;

public static class LicenseDocument
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDocumentBytes = 64 * 1024;
    public const int MaximumPayloadBytes = 16 * 1024;

    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Sign(LicensePayload payload, ReadOnlySpan<byte> privateKeyPkcs8)
    {
        ValidatePayloadShape(payload);
        var payloadBytes = SerializePayload(payload);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out var consumed);
        if (consumed != privateKeyPkcs8.Length)
            throw new CryptographicException("签发私钥包含无法识别的尾部数据。");
        var signature = key.SignData(payloadBytes, HashAlgorithmName.SHA256);
        var envelope = new LicenseEnvelope(
            CurrentSchemaVersion,
            payload.KeyId,
            Base64Url.Encode(payloadBytes),
            Base64Url.Encode(signature));
        return JsonSerializer.Serialize(envelope, EnvelopeOptions);
    }

    public static LicenseValidationResult Validate(
        string? document,
        string expectedKeyId,
        ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo,
        string expectedProduct,
        string expectedDeviceCode,
        int currentMajorVersion,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(document))
            return Failure(LicenseValidationStatus.Missing, "尚未导入许可证。");
        if (Encoding.UTF8.GetByteCount(document) > MaximumDocumentBytes)
            return Failure(LicenseValidationStatus.Malformed, "许可证文件过大或格式不正确。");

        try
        {
            var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(document, EnvelopeOptions);
            if (envelope is null
                || envelope.SchemaVersion != CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(envelope.KeyId)
                || string.IsNullOrWhiteSpace(envelope.Payload)
                || string.IsNullOrWhiteSpace(envelope.Signature))
                return Failure(LicenseValidationStatus.Malformed, "许可证结构不完整。");
            if (!string.Equals(envelope.KeyId, expectedKeyId, StringComparison.Ordinal))
                return Failure(LicenseValidationStatus.UnknownKey, "许可证由未知签发密钥生成。");

            var payloadBytes = Base64Url.Decode(envelope.Payload, MaximumPayloadBytes);
            var signature = Base64Url.Decode(envelope.Signature, 512);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySubjectPublicKeyInfo, out var consumed);
            if (consumed != publicKeySubjectPublicKeyInfo.Length
                || !key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
                return Failure(LicenseValidationStatus.InvalidSignature, "许可证签名无效，文件可能已被修改。");

            var payload = DeserializePayload(payloadBytes);
            ValidatePayloadShape(payload);
            if (!string.Equals(payload.KeyId, envelope.KeyId, StringComparison.Ordinal))
                return Failure(LicenseValidationStatus.InvalidSignature, "许可证密钥标识不一致。");
            if (!string.Equals(payload.Product, expectedProduct, StringComparison.Ordinal))
                return Failure(LicenseValidationStatus.WrongProduct, "这份许可证不适用于 FR_Imageprompt。");
            if (!string.Equals(
                    DeviceRequestCode.Normalize(payload.DeviceCode),
                    DeviceRequestCode.Normalize(expectedDeviceCode),
                    StringComparison.Ordinal))
                return Failure(LicenseValidationStatus.WrongDevice, "许可证与当前电脑不匹配。");
            if (payload.IssuedAtUtc > nowUtc.AddMinutes(5))
                return Failure(LicenseValidationStatus.NotYetValid, "系统时间早于许可证签发时间，请检查 Windows 日期和时间。");
            if (payload.ExpiresAtUtc is { } expiry && nowUtc >= expiry)
                return Failure(LicenseValidationStatus.Expired, $"许可证已于 {expiry.ToLocalTime():yyyy-MM-dd} 到期。");
            if (currentMajorVersion < payload.MinimumMajorVersion
                || currentMajorVersion > payload.MaximumMajorVersion)
                return Failure(LicenseValidationStatus.VersionNotCovered, "许可证不覆盖当前软件大版本。");

            var duration = payload.ExpiresAtUtc is null
                ? "永久"
                : $"有效至 {payload.ExpiresAtUtc.Value.ToLocalTime():yyyy-MM-dd}";
            return new LicenseValidationResult(
                LicenseValidationStatus.Valid,
                $"已授权给 {payload.Licensee} · {duration}",
                payload);
        }
        catch (Exception exception) when (exception is JsonException
                                               or FormatException
                                               or ArgumentException
                                               or CryptographicException
                                               or InvalidDataException)
        {
            return Failure(LicenseValidationStatus.Malformed, "许可证格式损坏或无法验证。");
        }
    }

    public static byte[] ExportPublicKey(ReadOnlySpan<byte> privateKeyPkcs8)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        return key.ExportSubjectPublicKeyInfo();
    }

    private static LicensePayload DeserializePayload(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        var root = document.RootElement;
        return new LicensePayload(
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("product").GetString() ?? "",
            root.GetProperty("licenseId").GetGuid(),
            root.GetProperty("licensee").GetString() ?? "",
            root.GetProperty("deviceCode").GetString() ?? "",
            root.GetProperty("issuedAtUtc").GetDateTimeOffset(),
            root.TryGetProperty("expiresAtUtc", out var expiry) && expiry.ValueKind != JsonValueKind.Null
                ? expiry.GetDateTimeOffset()
                : null,
            root.GetProperty("minimumMajorVersion").GetInt32(),
            root.GetProperty("maximumMajorVersion").GetInt32(),
            root.GetProperty("keyId").GetString() ?? "");
    }

    private static byte[] SerializePayload(LicensePayload payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", payload.SchemaVersion);
            writer.WriteString("product", payload.Product);
            writer.WriteString("licenseId", payload.LicenseId);
            writer.WriteString("licensee", payload.Licensee);
            writer.WriteString("deviceCode", DeviceRequestCode.Normalize(payload.DeviceCode));
            writer.WriteString("issuedAtUtc", payload.IssuedAtUtc.ToUniversalTime());
            if (payload.ExpiresAtUtc is { } expiry)
                writer.WriteString("expiresAtUtc", expiry.ToUniversalTime());
            else
                writer.WriteNull("expiresAtUtc");
            writer.WriteNumber("minimumMajorVersion", payload.MinimumMajorVersion);
            writer.WriteNumber("maximumMajorVersion", payload.MaximumMajorVersion);
            writer.WriteString("keyId", payload.KeyId);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void ValidatePayloadShape(LicensePayload payload)
    {
        if (payload.SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("不支持的许可证版本。");
        if (string.IsNullOrWhiteSpace(payload.Product) || payload.Product.Length > 80) throw new InvalidDataException("产品字段无效。");
        if (payload.LicenseId == Guid.Empty) throw new InvalidDataException("许可证 ID 无效。");
        if (string.IsNullOrWhiteSpace(payload.Licensee) || payload.Licensee.Trim().Length > 200) throw new InvalidDataException("使用人字段无效。");
        if (!DeviceRequestCode.IsValid(payload.DeviceCode)) throw new InvalidDataException("设备请求码无效。");
        if (payload.MinimumMajorVersion < 1 || payload.MaximumMajorVersion < payload.MinimumMajorVersion) throw new InvalidDataException("版本范围无效。");
        if (string.IsNullOrWhiteSpace(payload.KeyId) || payload.KeyId.Length > 80) throw new InvalidDataException("密钥标识无效。");
        if (payload.ExpiresAtUtc is { } expiry && expiry <= payload.IssuedAtUtc) throw new InvalidDataException("许可证到期时间无效。");
    }

    private static LicenseValidationResult Failure(LicenseValidationStatus status, string message) => new(status, message);
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static byte[] Decode(string value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumBytes * 2) throw new FormatException();
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        var bytes = Convert.FromBase64String(normalized);
        if (bytes.Length > maximumBytes) throw new FormatException();
        return bytes;
    }
}
