namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Returns detached review evidence and current value-free effect evidence.</summary>
/// <param name="Status">The canonical review evidence outcome.</param>
/// <param name="Evidence">The append-only detached evidence chain.</param>
/// <param name="EffectEvidence">The value-free effect posture when the request names an effect.</param>
public sealed record HumanReviewEvidenceReadResult(HumanReviewEvidenceReadStatus Status, IReadOnlyList<HumanReviewEvidenceProjection> Evidence, HumanReviewEffectEvidence? EffectEvidence)
{
    /// <summary>Gets an immutable defensive copy of the evidence chain.</summary>
    public IReadOnlyList<HumanReviewEvidenceProjection> Evidence { get; } = Evidence is null ? null! : Array.AsReadOnly(Evidence.ToArray());
}
