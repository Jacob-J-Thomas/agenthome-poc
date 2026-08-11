using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority.Grants;

/// <summary>Identifies one stable revision-pinned authority grant without granting authority by itself.</summary>
public sealed class AuthorityGrantId : IEquatable<AuthorityGrantId>, IComparable<AuthorityGrantId>
{
    private AuthorityGrantId(string value) => Value = value;

    /// <summary>Gets the canonical grant token.</summary>
    public string Value { get; }

    /// <summary>Parses a bounded canonical grant token without normalization.</summary>
    public static bool TryParse(string? value, out AuthorityGrantId? id, out AuthorityContractError? error)
    {
        if (!AuthorityTextRules.IsToken(value, AuthorityGrantContractLimits.MaxGrantIdCharacters))
        {
            id = null;
            error = new AuthorityContractError(AuthorityContractErrorCode.Required, AuthorityContractField.Contract);
            return false;
        }

        id = new AuthorityGrantId(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(AuthorityGrantId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(AuthorityGrantId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorityGrantId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
