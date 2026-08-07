using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Identifies a replaceable credential-value provider without exposing its private locator.</summary>
public sealed class CredentialProviderId : IEquatable<CredentialProviderId>, IComparable<CredentialProviderId>
{
    private CredentialProviderId(string value) => Value = value;

    /// <summary>Gets the canonical provider identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a bounded canonical provider identifier.</summary>
    public static bool TryParse(string? value, out CredentialProviderId? id, out CredentialContractError? error)
    {
        if (!CredentialContractText.IsToken(value, CredentialContractLimits.MaxIdCharacters) || !value!.Contains('.', StringComparison.Ordinal))
        {
            id = null;
            error = CredentialContractError.Create(CredentialContractErrorCode.InvalidCredentialProviderId, "$");
            return false;
        }

        id = new CredentialProviderId(value);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CredentialProviderId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public bool Equals(CredentialProviderId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CredentialProviderId other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    /// <inheritdoc />
    public override string ToString() => Value;
}
