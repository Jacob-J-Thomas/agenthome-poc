using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Retains canonical Application recovery results only inside the Startup composition boundary.</summary>
internal sealed record HumanReviewRecoveryExecutionResult(
    HumanReviewRecoveryPassStatus Status,
    HumanReviewPublicationRecoveryResult Publication,
    HumanReviewContinuationRecoveryResult Continuation,
    HumanReviewDecisionActionRecoveryResult DecisionAction);
