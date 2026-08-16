using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

internal sealed record GovernedLoopCoordinatorHeartbeatRetirement(
    int SchemaVersion,
    GovernedLoopCoordinatorOwnership Ownership,
    long RetiredCount,
    string InitialHeartbeatHash,
    long RetiredThroughSequence,
    DateTimeOffset RetiredThroughRecordedAtUtc,
    DateTimeOffset RetiredThroughLeaseExpiresAtUtc,
    string RetiredThroughHeartbeatHash,
    string ChainHash,
    string ContentHash);
