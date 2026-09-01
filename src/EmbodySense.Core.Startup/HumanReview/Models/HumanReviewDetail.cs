namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Combines bounded request, decision, evidence, and runtime projections for one exact review.</summary>
/// <param name="Summary">The safe review summary.</param>
/// <param name="Previews">The request's bounded redacted previews.</param>
/// <param name="Decisions">The accepted detached decisions.</param>
/// <param name="Evidence">The append-only detached evidence chain.</param>
/// <param name="Runtime">The current detached runtime posture.</param>
/// <param name="EffectEvidence">The current value-free effect evidence, when the request names an effect.</param>
public sealed record HumanReviewDetail(HumanReviewSummary Summary, IReadOnlyList<HumanReviewPreview> Previews, IReadOnlyList<HumanReviewDecisionProjection> Decisions, IReadOnlyList<HumanReviewEvidenceProjection> Evidence, HumanReviewRuntimePosture Runtime, HumanReviewEffectEvidence? EffectEvidence)
{
    /// <summary>Gets an immutable defensive copy of request previews.</summary>
    public IReadOnlyList<HumanReviewPreview> Previews { get; } = Previews is null ? null! : Array.AsReadOnly(Previews.ToArray());

    /// <summary>Gets an immutable defensive copy of accepted decisions.</summary>
    public IReadOnlyList<HumanReviewDecisionProjection> Decisions { get; } = Decisions is null ? null! : Array.AsReadOnly(Decisions.ToArray());

    /// <summary>Gets an immutable defensive copy of evidence.</summary>
    public IReadOnlyList<HumanReviewEvidenceProjection> Evidence { get; } = Evidence is null ? null! : Array.AsReadOnly(Evidence.ToArray());
}
