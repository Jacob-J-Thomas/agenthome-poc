using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one checkpoint publication mutation.</summary>
/// <param name="Status">The closed store outcome.</param>
/// <param name="Checkpoint">The authenticated durable checkpoint when available.</param>
public sealed record GovernedLoopSleepCheckpointMutationResult(
    GovernedLoopSleepCheckpointMutationStatus Status,
    GovernedLoopSleepCheckpoint? Checkpoint = null);
