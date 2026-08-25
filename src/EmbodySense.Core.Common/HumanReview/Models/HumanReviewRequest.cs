using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines one immutable schema-1 Human Review request intent; it records consent options and exact bounds but grants no authority or executable behavior.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="RequestId">The globally unique immutable review request identity.</param>
/// <param name="RequestOperationId">The globally unique admission operation identity.</param>
/// <param name="Binding">The exact immutable run, revision, node, activation or visit, attempt, frontier, and evidence binding.</param>
/// <param name="Purpose">The exact continuation or pre-dispatch effect purpose.</param>
/// <param name="RequestedDecisions">The canonical ordered closed decision vocabulary offered to the reviewer.</param>
/// <param name="EligibleReviewers">The canonical ordered exact reviewer role and scope set.</param>
/// <param name="ApprovalScope">The exact consent scope that cannot widen the binding.</param>
/// <param name="Previews">The canonical ordered, bounded, redacted action/result/evidence previews.</param>
/// <param name="Timing">The finite trusted UTC creation, due, and expiry boundaries.</param>
/// <param name="Provenance">The immutable trusted creation provenance.</param>
/// <param name="RequestHash">The canonical hash of every behavior-affecting request field.</param>
public sealed partial record HumanReviewRequest(
    int SchemaVersion,
    string RequestId,
    string RequestOperationId,
    HumanReviewBinding Binding,
    HumanReviewPurpose Purpose,
    ImmutableArray<HumanReviewDecisionKind> RequestedDecisions,
    ImmutableArray<HumanReviewReviewerScope> EligibleReviewers,
    HumanReviewApprovalScope ApprovalScope,
    ImmutableArray<HumanReviewRedactedPreview> Previews,
    HumanReviewTiming Timing,
    HumanReviewProvenance Provenance,
    string RequestHash)
{
    /// <summary>Gets the only supported request schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;

}
