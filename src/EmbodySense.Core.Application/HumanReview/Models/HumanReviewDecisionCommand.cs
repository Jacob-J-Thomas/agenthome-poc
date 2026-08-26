using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies only the bounded client-owned decision fields and predecessor version.</summary>
/// <param name="RunId">The target run identity.</param>
/// <param name="ExpectedLifecycleVersion">The caller's positive exact predecessor run lifecycle version, used only after exact-operation replay lookup.</param>
/// <param name="DecisionOperationId">The client idempotency identity.</param>
/// <param name="Kind">The closed requested decision kind.</param>
/// <param name="Detail">Optional bounded redacted detail.</param>
public sealed record HumanReviewDecisionCommand(string RunId, int ExpectedLifecycleVersion, string DecisionOperationId, HumanReviewDecisionKind Kind, string? Detail);
