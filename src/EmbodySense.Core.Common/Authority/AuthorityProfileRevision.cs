using System.Globalization;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Identifies one positive, canonical revision of an authority profile.
/// </summary>
public sealed class AuthorityProfileRevision : IEquatable<AuthorityProfileRevision>, IComparable<AuthorityProfileRevision>
{
    private AuthorityProfileRevision(int value)
    {
        Value = value;
    }

    /// <summary>Gets the positive revision number.</summary>
    public int Value { get; }

    /// <summary>
    /// Parses a positive base-10 revision without leading zeroes.
    /// </summary>
    /// <param name="value">The candidate revision.</param>
    /// <param name="revision">The parsed revision when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when the revision is canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out AuthorityProfileRevision? revision, out AuthorityContractError? error)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 10 || value[0] == '0' || value.Any(character => character is < '0' or > '9') || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            revision = null;
            error = new AuthorityContractError(AuthorityContractErrorCode.InvalidRevision, AuthorityContractField.Revision);
            return false;
        }

        revision = new AuthorityProfileRevision(parsed);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(AuthorityProfileRevision? other) => other is null ? 1 : Value.CompareTo(other.Value);

    /// <inheritdoc />
    public bool Equals(AuthorityProfileRevision? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorityProfileRevision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
