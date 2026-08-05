using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Represents the SHA-256 digest of one canonical credential contract.</summary>
public sealed class CredentialContractHash : IEquatable<CredentialContractHash>
{
    private CredentialContractHash(CapabilityIntegrityDigest digest) => Value = digest.Value;

    /// <summary>Gets the canonical digest.</summary>
    public string Value { get; }

    /// <summary>Computes a digest over an already canonical contract.</summary>
    public static CredentialContractHash Compute(string canonicalContract)
    {
        ArgumentNullException.ThrowIfNull(canonicalContract);
        return new CredentialContractHash(CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(canonicalContract)));
    }

    /// <summary>Parses a canonical SHA-256 credential contract digest.</summary>
    public static bool TryParse(string? value, out CredentialContractHash? hash, out CredentialContractError? error)
    {
        if (!CapabilityIntegrityDigest.TryParse(value, out var digest, out _))
        {
            hash = null;
            error = CredentialContractError.Create(CredentialContractErrorCode.InvalidCredentialContractHash, "$");
            return false;
        }

        hash = new CredentialContractHash(digest!);
        error = null;
        return true;
    }

    /// <summary>Compares canonical digest bytes in fixed time.</summary>
    public bool FixedTimeEquals(CredentialContractHash? other) => other is not null && CapabilityIntegrityDigest.TryParse(Value, out var left, out _) && CapabilityIntegrityDigest.TryParse(other.Value, out var right, out _) && left!.FixedTimeEquals(right);
    /// <inheritdoc />
    public bool Equals(CredentialContractHash? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CredentialContractHash other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    /// <inheritdoc />
    public override string ToString() => Value;
}
