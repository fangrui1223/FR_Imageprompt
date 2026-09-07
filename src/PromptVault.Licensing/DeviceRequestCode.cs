using System.Security.Cryptography;
using System.Text;

namespace PromptVault.Licensing;

public static class DeviceRequestCode
{
    private const string Prefix = "FR2";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Create(string machineGuid, uint systemVolumeSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineGuid);
        var normalizedGuid = machineGuid.Trim().ToUpperInvariant();
        var input = $"FR_Imageprompt.Device.V1|{normalizedGuid}|{systemVolumeSerial:X8}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var encoded = EncodeBase32(hash.AsSpan(0, 16));
        return Prefix + "-" + string.Join('-', Chunk(encoded, 5));
    }

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    }

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = Normalize(value);
        if (!normalized.StartsWith(Prefix + "-", StringComparison.Ordinal)) return false;
        var compact = normalized[(Prefix.Length + 1)..].Replace("-", "", StringComparison.Ordinal);
        return compact.Length == 26 && compact.All(character => Alphabet.Contains(character));
    }

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static IEnumerable<string> Chunk(string value, int length)
    {
        for (var index = 0; index < value.Length; index += length)
        {
            yield return value.Substring(index, Math.Min(length, value.Length - index));
        }
    }
}
