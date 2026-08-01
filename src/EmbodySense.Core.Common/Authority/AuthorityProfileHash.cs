using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Represents the canonical SHA-256 identity of one validated authority profile revision.
/// </summary>
public sealed class AuthorityProfileHash : IEquatable<AuthorityProfileHash>
{
    private const string Prefix = "sha256:";

    private AuthorityProfileHash(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical lowercase <c>sha256:</c>-prefixed profile hash.</summary>
    public string Value { get; }

    /// <summary>
    /// Computes the hash of canonical authority-profile JSON after complete validation.
    /// </summary>
    /// <param name="profile">The profile to validate and hash.</param>
    /// <param name="hash">The profile hash when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when hashing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryCompute(AuthorityProfile? profile, out AuthorityProfileHash? hash, out AuthorityContractValidationResult validation)
    {
        if (!AuthorityProfileJson.TrySerialize(profile, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = new AuthorityProfileHash(Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json!))).ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// Parses a canonical lowercase SHA-256 profile hash.
    /// </summary>
    /// <param name="value">The candidate hash.</param>
    /// <param name="hash">The parsed hash when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when the hash is canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out AuthorityProfileHash? hash, out AuthorityContractError? error)
    {
        if (value?.Length != Prefix.Length + 64 || !value.StartsWith(Prefix, StringComparison.Ordinal) || value[Prefix.Length..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            hash = null;
            error = new AuthorityContractError(AuthorityContractErrorCode.Required, AuthorityContractField.Contract);
            return false;
        }

        hash = new AuthorityProfileHash(value);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(AuthorityProfileHash? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthorityProfileHash other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
