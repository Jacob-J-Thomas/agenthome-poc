namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one append-only Human Review evidence artifact by its stable identity and canonical hash.</summary>
/// <param name="EvidenceId">The globally unique evidence identity.</param>
/// <param name="EvidenceHash">The exact canonical evidence hash.</param>
public sealed record HumanReviewEvidenceReference(string EvidenceId, string EvidenceHash);
