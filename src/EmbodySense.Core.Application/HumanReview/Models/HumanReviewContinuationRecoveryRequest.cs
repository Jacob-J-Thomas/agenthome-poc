namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies one bounded host-neutral recovery pass and its durable worker identity.</summary>
/// <param name="MaximumCount">The bounded number of canonical run summaries to scan.</param>
/// <param name="ScanCursor">The opaque exclusive cursor returned by the preceding pass, or null to start or restart from the beginning.</param>
/// <param name="WorkerId">The canonical durable worker identity attached to a successful claim.</param>
/// <param name="CoordinatorSourceId">The canonical coordinator source identity retained in claim and retirement provenance.</param>
/// <param name="ClaimLeaseDuration">The positive bounded exclusive claim lease duration.</param>
public sealed record HumanReviewContinuationRecoveryRequest(
    int MaximumCount,
    string? ScanCursor,
    string WorkerId,
    string CoordinatorSourceId,
    TimeSpan ClaimLeaseDuration);
