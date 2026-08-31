namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Projects detached current effect-attempt evidence without exposing effect values or workspace binding.</summary>
/// <param name="Status">The fail-closed release-relevant evidence status.</param>
/// <param name="Certainty">The current dispatch certainty when available.</param>
/// <param name="IdentityHash">The canonical current attempt identity hash when available.</param>
/// <param name="PreparationHash">The canonical current preparation hash when available.</param>
/// <param name="EffectAttemptId">The bounded effect attempt identity when available.</param>
public sealed record HumanReviewEffectEvidence(HumanReviewEffectEvidenceStatus Status, HumanReviewEffectCertainty? Certainty, string? IdentityHash, string? PreparationHash, string? EffectAttemptId);
