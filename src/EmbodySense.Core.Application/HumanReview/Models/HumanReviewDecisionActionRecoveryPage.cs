namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one bounded discovery page and its opaque exclusive cursor.</summary>
public sealed record HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus Status, IReadOnlyList<HumanReviewDecisionActionRecoveryCandidate> Candidates, string? NextScanCursor, bool SourceTruncated);
