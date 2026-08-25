namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable reviewer decision and its client-supplied idempotent operation identity.</summary>
/// <param name="DecisionId">The globally unique accepted or attempted decision identity.</param>
/// <param name="DecisionOperationId">The client-supplied globally unique idempotency operation identity.</param>
/// <param name="Kind">The closed decision kind retained by the decision artifact.</param>
/// <param name="DecisionHash">The exact canonical decision hash.</param>
public sealed record HumanReviewDecisionReference(string DecisionId, string DecisionOperationId, HumanReviewDecisionKind Kind, string DecisionHash);
