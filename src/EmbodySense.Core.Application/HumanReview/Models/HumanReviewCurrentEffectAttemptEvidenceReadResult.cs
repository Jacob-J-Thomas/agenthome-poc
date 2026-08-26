namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one closed current-effect-attempt evidence result and, only when current, detached value-free evidence.</summary>
/// <param name="Status">The closed canonical evidence-read posture.</param>
/// <param name="Evidence">The detached current identity/preparation evidence only when <paramref name="Status"/> is <see cref="HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current"/>.</param>
public sealed record HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus Status, HumanReviewCurrentEffectAttemptEvidence? Evidence = null);
