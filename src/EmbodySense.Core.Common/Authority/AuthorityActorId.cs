using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Identifies the human or system actor recorded as provenance without asserting that actor's trust or authority.
/// </summary>
public sealed class AuthorityActorId : IEquatable<AuthorityActorId>, IComparable<AuthorityActorId>
{
    private AuthorityActorId(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical actor token.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a canonical actor token without normalization or case folding.
    /// </summary>
    /// <param name="value">The candidate actor identifier.</param>
    /// <param name="id">The parsed actor identifier when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when the identifier is canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out AuthorityActorId? id, out AuthorityContractError? error)
    {
        if (!AuthorityTextRules.IsToken(value, AuthorityContractLimits.MaxActorIdCharacters))
        {
            id = null;
            error = new AuthorityContractError(AuthorityContractErrorCode.InvalidActorId, AuthorityContractField.ProvenanceActorId);
            return false;
        }

        id = new AuthorityActorId(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(AuthorityActorId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(AuthorityActorId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorityActorId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
