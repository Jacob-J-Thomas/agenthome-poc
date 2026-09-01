namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Projects one append-only value-free review evidence artifact.</summary>
/// <param name="EvidenceId">The durable evidence identity.</param>
/// <param name="Kind">The closed evidence kind.</param>
/// <param name="RecordedAtUtc">The trusted recording time.</param>
/// <param name="DecisionOperationId">The related operation identity when present.</param>
/// <param name="DecisionKind">The related decision kind when present.</param>
/// <param name="Previews">Bounded redacted evidence previews.</param>
/// <param name="EvidenceHash">The canonical evidence hash.</param>
public sealed record HumanReviewEvidenceProjection(string EvidenceId, HumanReviewEvidenceKind Kind, DateTimeOffset RecordedAtUtc, string? DecisionOperationId, HumanReviewDecisionKind? DecisionKind, IReadOnlyList<HumanReviewPreview> Previews, string EvidenceHash)
{
    /// <summary>Gets an immutable defensive copy of evidence previews.</summary>
    public IReadOnlyList<HumanReviewPreview> Previews { get; } = Previews is null ? null! : Array.AsReadOnly(Previews.ToArray());
}
