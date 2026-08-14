using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Computes and verifies the canonical pure-node outcome payload digest.</summary>
public static class GovernedLoopPureNodeOutcomeHash
{
    /// <summary>Recomputes the lowercase SHA-256 digest of the exact canonical outcome payload.</summary>
    /// <param name="outcome">The validated immutable outcome.</param>
    /// <returns>The lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outcome"/> is <see langword="null"/>.</exception>
    public static string Compute(GovernedLoopPureNodeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return ComputeCanonical(outcome.CanonicalPayloadJson);
    }

    /// <summary>Returns whether a claimed digest exactly identifies the outcome payload.</summary>
    /// <param name="outcome">The validated immutable outcome.</param>
    /// <param name="claimedHash">The claimed lowercase SHA-256 digest.</param>
    /// <returns><see langword="true"/> only for an exact canonical match.</returns>
    public static bool Matches(GovernedLoopPureNodeOutcome? outcome, string? claimedHash)
    {
        if (outcome is null || claimedHash is not { Length: 64 } || claimedHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            return false;
        }

        var expected = Compute(outcome);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(claimedHash));
    }

    internal static string ComputeCanonical(string canonicalPayloadJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayloadJson))).ToLowerInvariant();
}
