using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Identifies an authority proof, operation, run, or evidence record within credential contracts.</summary>
public sealed class CredentialContractId : IEquatable<CredentialContractId>, IComparable<CredentialContractId>
{
    private CredentialContractId(string value) => Value = value;

    /// <summary>Gets the canonical identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a bounded canonical contract identifier.</summary>
    public static bool TryParse(string? value, out CredentialContractId? id, out CredentialContractError? error)
    {
        if (!CredentialContractText.IsToken(value, CredentialContractLimits.MaxIdCharacters))
        {
            id = null;
            error = CredentialContractError.Create(CredentialContractErrorCode.InvalidCredentialContractId, "$");
            return false;
        }

        id = new CredentialContractId(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CredentialContractId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public bool Equals(CredentialContractId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CredentialContractId other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    /// <inheritdoc />
    public override string ToString() => Value;
}
