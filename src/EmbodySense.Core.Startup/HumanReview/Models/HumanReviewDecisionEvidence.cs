namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Projects a durable decision-operation receipt without reviewer identity, scope, or private detail.</summary>
/// <param name="OperationId">The exact operation identity.</param>
/// <param name="RequestId">The exact reviewed request identity.</param>
/// <param name="Disposition">The durable operation disposition.</param>
/// <param name="DecisionKind">The accepted decision kind when present.</param>
/// <param name="RecordedAtUtc">The trusted receipt time.</param>
/// <param name="ProposalHash">The canonical bounded proposal hash.</param>
/// <param name="ReceiptHash">The canonical receipt hash.</param>
public sealed record HumanReviewDecisionEvidence(string OperationId, string RequestId, HumanReviewDecisionOperationDisposition Disposition, HumanReviewDecisionKind? DecisionKind, DateTimeOffset RecordedAtUtc, string ProposalHash, string ReceiptHash);
