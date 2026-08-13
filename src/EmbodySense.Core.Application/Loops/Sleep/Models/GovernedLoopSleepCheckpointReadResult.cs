using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one deterministic checkpoint read.</summary>
/// <param name="Status">The closed read status.</param>
/// <param name="Checkpoint">The checkpoint, present exactly when found.</param>
public sealed record GovernedLoopSleepCheckpointReadResult(
    GovernedLoopSleepStoreReadStatus Status,
    GovernedLoopSleepCheckpoint? Checkpoint = null);
