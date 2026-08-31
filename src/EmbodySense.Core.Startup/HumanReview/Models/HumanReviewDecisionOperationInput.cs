namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Supplies only the bounded client-owned fields accepted by the Human Review decision surface.</summary>
/// <param name="RunId">The target durable run identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic review lifecycle version.</param>
/// <param name="DecisionOperationId">The client idempotency identity.</param>
/// <param name="Kind">The requested closed decision kind.</param>
/// <param name="Detail">Optional bounded redacted detail, required only for RequestInformation.</param>
public sealed record HumanReviewDecisionOperationInput(string RunId, int ExpectedLifecycleVersion, string DecisionOperationId, HumanReviewDecisionKind Kind, string? Detail);
