using System.Text;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>Computes the canonical SHA-256 identity of a validated dependency manifest.</summary>
public sealed class CapabilityDependencyManifestHash : IEquatable<CapabilityDependencyManifestHash>
{
    private CapabilityDependencyManifestHash(CapabilityIntegrityDigest digest) => Value = digest.Value;

    /// <summary>Gets the canonical SHA-256 digest.</summary>
    public string Value { get; }

    /// <summary>Computes a hash after complete manifest validation.</summary>
    public static bool TryCompute(CapabilityDependencyManifest? manifest, out CapabilityDependencyManifestHash? hash, out CapabilityContractValidationResult validation)
    {
        if (!CapabilityDependencyManifestJson.TrySerialize(manifest, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = new CapabilityDependencyManifestHash(CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(json!)));
        return true;
    }

    /// <inheritdoc />
    public bool Equals(CapabilityDependencyManifestHash? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CapabilityDependencyManifestHash other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}
