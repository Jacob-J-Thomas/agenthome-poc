namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable decision-operation receipt by operation, proposal, disposition, and receipt hash.</summary>
/// <param name="DecisionOperationId">The exact client operation identity.</param>
/// <param name="ProposalHash">The exact server-derived proposal hash.</param>
/// <param name="Disposition">The exact durable outcome.</param>
/// <param name="ReceiptHash">The exact canonical receipt hash.</param>
public sealed record HumanReviewDecisionOperationReference(string DecisionOperationId, string ProposalHash, HumanReviewDecisionOperationDisposition Disposition, string ReceiptHash);
