namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable published non-approval decision-action wake.</summary>
/// <param name="WakeId">The wake identity.</param>
/// <param name="WakeHash">The canonical hash of the wake.</param>
public sealed record HumanReviewDecisionActionWakeReference(string WakeId, string WakeHash);
