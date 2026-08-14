using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Computes and verifies the exact canonical typed-value envelope digest.</summary>
public static class GovernedLoopTypedValueHash
{
    /// <summary>Computes a lowercase SHA-256 digest of the value's exact canonical envelope.</summary>
    /// <param name="value">The validated immutable typed value.</param>
    /// <returns>The lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    public static string Compute(GovernedLoopTypedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ComputeCanonical(value.CanonicalJson);
    }

    /// <summary>Returns whether a claimed digest exactly identifies the typed value.</summary>
    /// <param name="value">The immutable typed value.</param>
    /// <param name="claimedHash">The claimed lowercase SHA-256 digest.</param>
    /// <returns><see langword="true"/> only for an exact canonical match.</returns>
    public static bool Matches(GovernedLoopTypedValue? value, string? claimedHash)
    {
        if (value is null || claimedHash is not { Length: 64 } || claimedHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            return false;
        }

        var expected = Compute(value);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(claimedHash));
    }

    internal static string ComputeCanonical(string canonicalJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}
