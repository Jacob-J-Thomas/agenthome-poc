using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Creates domain-separated canonical lowercase SHA-256 fingerprints for workspace action evidence.</summary>
public static class WorkspaceActionFingerprint
{
    /// <summary>Computes a canonical fingerprint over length-prefixed scalar values.</summary>
    /// <param name="domain">The stable schema-1 domain separator.</param>
    /// <param name="values">The ordered canonical scalar values; null remains distinct from empty.</param>
    /// <returns>The lowercase SHA-256 fingerprint.</returns>
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

    /// <summary>Returns whether a value is a canonical lowercase SHA-256 fingerprint.</summary>
    public static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Returns whether a value is a bounded opaque evidence identifier.</summary>
    public static bool IsEvidenceIdentifier(string? value)
        => value is { Length: > 0 and <= WorkspaceActionContractLimits.MaxIdentifierCharacters }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or ':' or '/');

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Span<byte> absent = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(absent, -1);
            hash.AppendData(absent);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
