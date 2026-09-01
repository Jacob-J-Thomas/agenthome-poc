using System.Collections.Immutable;

namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Contains only the server-derived facts a Human Review authority source must bind.</summary>
/// <param name="RequestId">The immutable canonical request identity.</param>
/// <param name="RequestHash">The immutable canonical request hash.</param>
/// <param name="DecisionKind">The exact closed decision kind being evaluated.</param>
/// <param name="DecisionOperationId">The exact idempotency operation identity.</param>
/// <param name="ProposalHash">The canonical hash of the bounded decision proposal.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC instant at which the server evaluated this binding.</param>
/// <param name="EligibleReviewers">The detached ordered role and scope entries admitted by the persisted request.</param>
public sealed record HumanReviewDecisionAuthorizationRequest(
    string RequestId,
    string RequestHash,
    HumanReviewDecisionKind DecisionKind,
    string DecisionOperationId,
    string ProposalHash,
    DateTimeOffset EvaluatedAtUtc,
    ImmutableArray<HumanReviewDecisionAuthorizationEligibility> EligibleReviewers);
