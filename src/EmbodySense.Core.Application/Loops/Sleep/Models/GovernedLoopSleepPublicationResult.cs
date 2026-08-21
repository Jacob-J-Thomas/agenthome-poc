using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports the durable outcome of one sleep publication attempt.</summary>
/// <param name="Status">The closed operation status.</param>
/// <param name="Checkpoint">The exact durable checkpoint when one was authenticated.</param>
public sealed record GovernedLoopSleepPublicationResult(
    GovernedLoopSleepPublicationStatus Status,
    GovernedLoopSleepCheckpoint? Checkpoint = null);
