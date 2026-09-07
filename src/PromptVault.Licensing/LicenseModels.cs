namespace PromptVault.Licensing;

public sealed record LicensePayload(
    int SchemaVersion,
    string Product,
    Guid LicenseId,
    string Licensee,
    string DeviceCode,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    int MinimumMajorVersion,
    int MaximumMajorVersion,
    string KeyId);

public sealed record LicenseEnvelope(
    int SchemaVersion,
    string KeyId,
    string Payload,
    string Signature);

public enum LicenseValidationStatus
{
    Valid,
    Missing,
    Unreadable,
    Malformed,
    UnknownKey,
    InvalidSignature,
    WrongProduct,
    WrongDevice,
    NotYetValid,
    Expired,
    VersionNotCovered
}

public sealed record LicenseValidationResult(
    LicenseValidationStatus Status,
    string Message,
    LicensePayload? License = null)
{
    public bool IsValid => Status == LicenseValidationStatus.Valid;
}
