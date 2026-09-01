namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Configures the bounded process-local Human Review recovery lanes.</summary>
/// <param name="MaximumCount">The maximum canonical run summaries examined by each lane in one pass.</param>
/// <param name="WorkerId">The durable-safe worker identity attached to continuation and action claims.</param>
/// <param name="CoordinatorSourceId">The coordinator provenance identity retained by continuation claims and retirements.</param>
/// <param name="ClaimLeaseDuration">The bounded claim lease shared by the two claimable recovery coordinators.</param>
public sealed record HumanReviewRecoveryRunnerOptions(
    int MaximumCount,
    string WorkerId,
    string CoordinatorSourceId,
    TimeSpan ClaimLeaseDuration);
