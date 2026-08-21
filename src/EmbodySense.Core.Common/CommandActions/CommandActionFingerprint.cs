using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.CommandActions;

/// <summary>Computes domain-separated length-prefixed command-action identities.</summary>
public static class CommandActionFingerprint
{
    /// <summary>Computes a canonical lowercase SHA-256 fingerprint.</summary>
    public static string Compute(string domain, params string?[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(values);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var value in values)
        {
            Append(hash, value);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Gets whether a value is a canonical lowercase SHA-256 fingerprint.</summary>
    public static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Gets whether a value is one bounded opaque evidence identifier.</summary>
    public static bool IsEvidenceIdentifier(string? value)
        => value is { Length: > 0 and <= 256 }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or ':' or '/');

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Span<byte> missing = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(missing, -1);
            hash.AppendData(missing);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
