namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable published continuation wake by its exact canonical hash.</summary>
/// <param name="WakeId">The globally unique wake identity.</param>
/// <param name="WakeHash">The exact canonical wake hash.</param>
public sealed record HumanReviewContinuationWakeReference(string WakeId, string WakeHash);
