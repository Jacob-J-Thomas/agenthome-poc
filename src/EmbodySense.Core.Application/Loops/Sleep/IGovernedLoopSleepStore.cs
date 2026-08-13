using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Persists immutable sleeping checkpoints and optimistic exactly-once wake evidence.</summary>
/// <remarks>Implementations make the checkpoint durable and retain the #314 frontier transaction fence; the #314 adapter owns release of the exact frontier. Stores must enforce at most one claimed wake per checkpoint across processes.</remarks>
public interface IGovernedLoopSleepStore
{
    /// <summary>Publishes one immutable checkpoint behind the exact waiting-frontier transaction fence.</summary>
    /// <remarks>
    /// <paramref name="expectedPostureHash"/> is the #314 executable Wait/frontier transaction seam. Concrete #336 durability retains and fences it; #314 supplies the frontier adapter that makes the transaction executable.
    /// </remarks>
    Task<GovernedLoopSleepCheckpointMutationResult?> PublishAndReleaseAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        string expectedPostureHash,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one checkpoint by its deterministic identity.</summary>
    Task<GovernedLoopSleepCheckpointReadResult?> ReadCheckpointAsync(string checkpointId, CancellationToken cancellationToken = default);

    /// <summary>Reads one wake delivery by its deterministic identity.</summary>
    Task<GovernedLoopWakeEvidenceReadResult?> ReadWakeAsync(string wakeId, CancellationToken cancellationToken = default);

    /// <summary>Claims one checkpoint with its initial terminal disposition or durable prepared continuation intent.</summary>
    /// <remarks>
    /// <paramref name="wakeClaimPostureHash"/> is the fresh current-posture proof already resolved by the application
    /// service. Concrete #336 durability retains it separately from immutable checkpoint-publication posture and permits
    /// only exact claim replay. #314 supplies the frontier adapter that makes the continuation transaction executable.
    /// </remarks>
    Task<GovernedLoopWakeEvidenceMutationResult?> CreateWakeAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence evidence,
        string wakeClaimPostureHash,
        CancellationToken cancellationToken = default);

    /// <summary>Advances one exact optimistic wake state after reconciling or invoking its continuation.</summary>
    Task<GovernedLoopWakeEvidenceMutationResult?> AdvanceWakeAsync(
        GovernedLoopWakeEvidence current,
        GovernedLoopWakeEvidence next,
        CancellationToken cancellationToken = default);
}
