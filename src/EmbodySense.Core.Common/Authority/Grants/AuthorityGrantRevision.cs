using System.Globalization;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority.Grants;

/// <summary>Identifies one positive immutable revision of an authority grant.</summary>
public sealed class AuthorityGrantRevision : IEquatable<AuthorityGrantRevision>, IComparable<AuthorityGrantRevision>
{
    private AuthorityGrantRevision(int value) => Value = value;

    /// <summary>Gets the positive revision number.</summary>
    public int Value { get; }

    /// <summary>Parses a canonical positive base-10 revision without leading zeroes.</summary>
    public static bool TryParse(string? value, out AuthorityGrantRevision? revision, out AuthorityContractError? error)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 10 || value[0] == '0' || value.Any(character => character is < '0' or > '9') || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            revision = null;
            error = new AuthorityContractError(AuthorityContractErrorCode.InvalidRevision, AuthorityContractField.Revision);
            return false;
        }

        revision = new AuthorityGrantRevision(parsed);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(AuthorityGrantRevision? other) => other is null ? 1 : Value.CompareTo(other.Value);

    /// <inheritdoc />
    public bool Equals(AuthorityGrantRevision? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorityGrantRevision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
