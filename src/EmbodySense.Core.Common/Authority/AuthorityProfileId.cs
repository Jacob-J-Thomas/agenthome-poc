using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Identifies one user-owned authority profile using a canonical, non-aliasing token.
/// </summary>
public sealed class AuthorityProfileId : IEquatable<AuthorityProfileId>, IComparable<AuthorityProfileId>
{
    private AuthorityProfileId(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical profile identifier.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a canonical profile identifier without normalization or case folding.
    /// </summary>
    /// <param name="value">The candidate profile identifier.</param>
    /// <param name="id">The parsed profile identifier when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when the identifier is canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out AuthorityProfileId? id, out AuthorityContractError? error)
    {
        if (!AuthorityTextRules.IsToken(value, AuthorityContractLimits.MaxProfileIdCharacters))
        {
            id = null;
            error = new AuthorityContractError(AuthorityContractErrorCode.InvalidProfileId, AuthorityContractField.ProfileId);
            return false;
        }

        id = new AuthorityProfileId(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(AuthorityProfileId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(AuthorityProfileId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorityProfileId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
