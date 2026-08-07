using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Identifies public credential metadata without identifying or carrying secret material.</summary>
public sealed class CredentialReferenceId : IEquatable<CredentialReferenceId>, IComparable<CredentialReferenceId>
{
    private CredentialReferenceId(string value) => Value = value;

    /// <summary>Gets the canonical identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a bounded canonical reference identifier.</summary>
    public static bool TryParse(string? value, out CredentialReferenceId? id, out CredentialContractError? error)
    {
        if (!CredentialContractText.IsToken(value, CredentialContractLimits.MaxIdCharacters))
        {
            id = null;
            error = CredentialContractError.Create(CredentialContractErrorCode.InvalidCredentialReferenceId, "$");
            return false;
        }

        id = new CredentialReferenceId(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CredentialReferenceId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public bool Equals(CredentialReferenceId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CredentialReferenceId other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    /// <inheritdoc />
    public override string ToString() => Value;
}
