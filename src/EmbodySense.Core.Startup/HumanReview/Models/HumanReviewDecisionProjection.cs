namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Projects one accepted decision without disclosing authenticated identity or reviewer role data.</summary>
/// <param name="DecisionId">The durable decision identity.</param>
/// <param name="OperationId">The idempotency operation identity.</param>
/// <param name="Kind">The closed decision kind.</param>
/// <param name="DecidedAtUtc">The trusted decision time.</param>
/// <param name="Detail">The bounded redacted reviewer detail.</param>
/// <param name="DecisionHash">The canonical decision hash.</param>
public sealed record HumanReviewDecisionProjection(string DecisionId, string OperationId, HumanReviewDecisionKind Kind, DateTimeOffset DecidedAtUtc, string? Detail, string DecisionHash);
