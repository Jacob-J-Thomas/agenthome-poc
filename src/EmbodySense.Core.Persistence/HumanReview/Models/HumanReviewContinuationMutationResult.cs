using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Persistence.HumanReview.Models;

/// <summary>Returns a closed canonical Human Review continuation mutation result and its detached canonical run when available.</summary>
/// <param name="Status">The closed mutation result.</param>
/// <param name="Run">The exact canonical run for a committed or replayed result, when it was safely observed.</param>
public sealed record HumanReviewContinuationMutationResult(HumanReviewContinuationMutationStatus Status, CustomLoopRunRecord? Run);
