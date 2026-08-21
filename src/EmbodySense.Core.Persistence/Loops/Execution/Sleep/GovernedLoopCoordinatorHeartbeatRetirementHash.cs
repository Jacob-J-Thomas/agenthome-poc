using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

internal static class GovernedLoopCoordinatorHeartbeatRetirementHash
{
    private static readonly string _emptyChainHash = new('0', 64);

    public static GovernedLoopCoordinatorHeartbeatRetirement Apply(GovernedLoopCoordinatorHeartbeatRetirement retirement)
        => retirement with { ContentHash = Compute(retirement) };

    public static bool Matches(GovernedLoopCoordinatorHeartbeatRetirement retirement)
        => IsHash(retirement.ContentHash)
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(retirement.ContentHash),
                Convert.FromHexString(Compute(retirement)));

    public static string Append(string? previousChainHash, string heartbeatHash)
    {
        var canonical = string.Join('\n', "governed-loop-coordinator-heartbeat-retirement-chain-v1", previousChainHash ?? _emptyChainHash, heartbeatHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Compute(GovernedLoopCoordinatorHeartbeatRetirement retirement)
    {
        var canonical = string.Join(
            '\n',
            "governed-loop-coordinator-heartbeat-retirement-v1",
            retirement.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            retirement.Ownership.ContentHash,
            retirement.RetiredCount.ToString(CultureInfo.InvariantCulture),
            retirement.InitialHeartbeatHash,
            retirement.RetiredThroughSequence.ToString(CultureInfo.InvariantCulture),
            retirement.RetiredThroughRecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            retirement.RetiredThroughLeaseExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
            retirement.RetiredThroughHeartbeatHash,
            retirement.ChainHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
