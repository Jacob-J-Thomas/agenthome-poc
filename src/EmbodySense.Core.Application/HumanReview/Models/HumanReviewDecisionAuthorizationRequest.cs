using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies the exact server-derived facts an authorizer must bind before a Human Review result can be used.</summary>
/// <param name="Request">The immutable persisted Human Review request being decided.</param>
/// <param name="Proposal">The canonical bounded client proposal.</param>
/// <param name="RequestHash">The exact immutable request hash that the response must echo.</param>
/// <param name="DecisionOperationId">The exact client idempotency identity that the response must echo.</param>
/// <param name="ProposalHash">The exact canonical proposal hash that the response must echo.</param>
/// <param name="EvaluatedAtUtc">The exact trusted UTC instant supplied by the decision service that the response must echo.</param>
public sealed record HumanReviewDecisionAuthorizationRequest(
    HumanReviewRequest Request,
    HumanReviewDecisionProposal Proposal,
    string RequestHash,
    string DecisionOperationId,
    string ProposalHash,
    DateTimeOffset EvaluatedAtUtc);
