using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines one immutable append-only Human Review evidence artifact without carrying credentials, raw payloads, or private data.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="EvidenceId">The globally unique append-only evidence identity.</param>
/// <param name="Request">The exact immutable request reference.</param>
/// <param name="Kind">The evidence event kind.</param>
/// <param name="Decision">The exact related decision reference when this evidence concerns a decision or approved continuation.</param>
/// <param name="RecordedAtUtc">The trusted UTC timestamp at which evidence was durably recorded.</param>
/// <param name="Provenance">The immutable trusted server or coordinator evidence provenance.</param>
/// <param name="Previews">The canonical ordered optional redacted evidence previews.</param>
/// <param name="PreviousEvidenceHash">The optional exact append-only predecessor evidence hash.</param>
/// <param name="EvidenceHash">The canonical hash of every behavior-affecting evidence field.</param>
public sealed partial record HumanReviewEvidence(
    int SchemaVersion,
    string EvidenceId,
    HumanReviewRequestReference Request,
    HumanReviewEvidenceKind Kind,
    HumanReviewDecisionReference? Decision,
    DateTimeOffset RecordedAtUtc,
    HumanReviewProvenance Provenance,
    ImmutableArray<HumanReviewRedactedPreview> Previews,
    string? PreviousEvidenceHash,
    string EvidenceHash)
{
    /// <summary>Gets the only supported evidence schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;

}
