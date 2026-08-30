namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one bounded recovery pass result.</summary>
public sealed record HumanReviewDecisionActionRecoveryResult(HumanReviewDecisionActionRecoveryStatus Status, string? NextScanCursor, bool SourceTruncated, IReadOnlyList<HumanReviewDecisionActionRecoveryItemResult> Items);
