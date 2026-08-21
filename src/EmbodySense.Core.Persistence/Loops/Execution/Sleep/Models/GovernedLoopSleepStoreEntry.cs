using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

internal sealed record GovernedLoopSleepStoreEntry(
    GovernedLoopSleepCheckpoint Checkpoint,
    string PublicationPostureHash,
    string? WakeClaimPostureHash,
    IReadOnlyList<GovernedLoopWakeEvidence> WakeEvidence);
