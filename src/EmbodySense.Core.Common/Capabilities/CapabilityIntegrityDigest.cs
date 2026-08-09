using EmbodySense.Core.Common.Capabilities.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents a canonical OCI-style SHA-256 content digest.
/// </summary>
[JsonConverter(typeof(CapabilityScalarJsonConverterFactory))]
public sealed class CapabilityIntegrityDigest : IEquatable<CapabilityIntegrityDigest>
{
    private const string Prefix = "sha256:";

    private CapabilityIntegrityDigest(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical <c>sha256:</c>-prefixed digest.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a lowercase SHA-256 digest.
    /// </summary>
    /// <param name="value">The candidate digest.</param>
    /// <param name="digest">The parsed digest when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityIntegrityDigest? digest, out CapabilityContractError? error)
    {
        if (value?.Length != Prefix.Length + 64 || !value.StartsWith(Prefix, StringComparison.Ordinal) || value[Prefix.Length..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            digest = null;
            error = new CapabilityContractError("invalid_integrity_digest", "$", "Integrity digests must use canonical lowercase OCI-style sha256 hexadecimal form.");
            return false;
        }

        digest = new CapabilityIntegrityDigest(value);
        error = null;
        return true;
    }

    /// <summary>
    /// Computes the canonical digest for a byte sequence.
    /// </summary>
    /// <param name="content">The content to hash.</param>
    /// <returns>The canonical SHA-256 digest.</returns>
    public static CapabilityIntegrityDigest Compute(ReadOnlySpan<byte> content)
    {
        return new CapabilityIntegrityDigest(Prefix + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
    }

    /// <summary>
    /// Compares a candidate digest in fixed time.
    /// </summary>
    /// <param name="other">The digest to compare.</param>
    /// <returns><see langword="true"/> when the digests are identical; otherwise, <see langword="false"/>.</returns>
    public bool FixedTimeEquals(CapabilityIntegrityDigest? other)
    {
        if (other is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Value), Encoding.ASCII.GetBytes(other.Value));
    }

    /// <inheritdoc />
    public bool Equals(CapabilityIntegrityDigest? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityIntegrityDigest other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
}
