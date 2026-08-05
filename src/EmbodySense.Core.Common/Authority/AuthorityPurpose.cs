using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Holds a bounded, normalized human-readable authority purpose without prompt or target content.
/// </summary>
public sealed class AuthorityPurpose : IEquatable<AuthorityPurpose>
{
    private AuthorityPurpose(string value)
    {
        Value = value;
    }

    /// <summary>Gets the validated purpose text.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses bounded NFC purpose text without unsafe Unicode.
    /// </summary>
    /// <param name="value">The candidate purpose text.</param>
    /// <param name="purpose">The parsed purpose when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when the purpose is safe and canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out AuthorityPurpose? purpose, out AuthorityContractError? error)
    {
        if (string.IsNullOrWhiteSpace(value) || !AuthorityTextRules.IsSafeNormalized(value, AuthorityContractLimits.MaxPurposeCharacters))
        {
            purpose = null;
            error = new AuthorityContractError(AuthorityContractErrorCode.InvalidPurpose, AuthorityContractField.Purpose);
            return false;
        }

        purpose = new AuthorityPurpose(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(AuthorityPurpose? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorityPurpose other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
