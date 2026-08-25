namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines one server-authored append-only receipt for a canonicalized reviewer decision operation.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="DecisionOperationId">The exact client idempotency identity.</param>
/// <param name="ProposalHash">The server-derived exact proposal hash.</param>
/// <param name="Request">The immutable review request reference.</param>
/// <param name="Disposition">The durable operation outcome.</param>
/// <param name="Decision">The accepted decision reference when the outcome accepted one.</param>
/// <param name="RecordedAtUtc">The trusted durable recording time.</param>
/// <param name="Provenance">The trusted server or coordinator provenance.</param>
/// <param name="ReceiptHash">The canonical hash of every behavior-affecting receipt field.</param>
public sealed record HumanReviewDecisionOperationReceipt(int SchemaVersion, string DecisionOperationId, string ProposalHash, HumanReviewRequestReference Request, HumanReviewDecisionOperationDisposition Disposition, HumanReviewDecisionReference? Decision, DateTimeOffset RecordedAtUtc, HumanReviewProvenance Provenance, string ReceiptHash)
{
    /// <summary>Gets the only supported receipt schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
