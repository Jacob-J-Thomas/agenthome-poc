namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Records the bounded non-secret outcome for one discovered continuation candidate.</summary>
/// <param name="RunId">The durable run identity.</param>
/// <param name="Status">The closed candidate outcome.</param>
public sealed record HumanReviewContinuationRecoveryItemResult(string RunId, HumanReviewContinuationRecoveryItemStatus Status);
