using EmbodySense.Core.Common.Capabilities.Models;
using System.Text;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents the canonical SHA-256 identity of one validated capability descriptor.
/// </summary>
[JsonConverter(typeof(CapabilityScalarJsonConverterFactory))]
public sealed class CapabilityDescriptorHash : IEquatable<CapabilityDescriptorHash>
{
    private CapabilityDescriptorHash(CapabilityIntegrityDigest digest)
    {
        Value = digest.Value;
    }

    /// <summary>Gets the canonical <c>sha256:</c>-prefixed descriptor hash.</summary>
    public string Value { get; }

    /// <summary>
    /// Computes the hash of the canonical descriptor JSON after complete validation.
    /// </summary>
    /// <param name="descriptor">The descriptor to validate and hash.</param>
    /// <param name="hash">The canonical descriptor hash when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the descriptor is valid and hashing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryCompute(CapabilityDescriptor? descriptor, out CapabilityDescriptorHash? hash, out CapabilityContractValidationResult validation)
    {
        if (!CapabilityDescriptorJson.TrySerialize(descriptor, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = new CapabilityDescriptorHash(CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(json!)));
        return true;
    }

    /// <summary>
    /// Parses a canonical descriptor hash.
    /// </summary>
    /// <param name="value">The candidate hash.</param>
    /// <param name="hash">The parsed hash when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityDescriptorHash? hash, out CapabilityContractError? error)
    {
        if (!CapabilityIntegrityDigest.TryParse(value, out var digest, out error))
        {
            hash = null;
            return false;
        }

        hash = new CapabilityDescriptorHash(digest!);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(CapabilityDescriptorHash? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityDescriptorHash other && Equals(other);
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
