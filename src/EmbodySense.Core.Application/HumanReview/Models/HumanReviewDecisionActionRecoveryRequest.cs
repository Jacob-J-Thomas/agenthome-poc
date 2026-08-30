namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Defines one bounded non-approval action recovery pass.</summary>
public sealed record HumanReviewDecisionActionRecoveryRequest(int MaximumCount, string? ScanCursor, string WorkerId, TimeSpan ClaimLease);
