using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

/// <summary>Reports the closed outcome of discovering and submitting one exact Human Input response wake.</summary>
/// <param name="Status">The closed response-continuation disposition.</param>
/// <param name="Wake">The generic durable wake result when a canonical wake was submitted.</param>
public sealed record HumanInputResponseContinuationWakeResult(
    HumanInputResponseContinuationWakeStatus Status,
    GovernedLoopWakeResult? Wake = null);
