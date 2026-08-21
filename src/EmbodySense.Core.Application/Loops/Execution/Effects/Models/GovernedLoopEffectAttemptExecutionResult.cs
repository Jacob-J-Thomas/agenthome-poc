using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Returns one bounded durable effect-attempt orchestration outcome.</summary>
/// <param name="Status">The closed execution posture.</param>
/// <param name="Attempt">The current durable value-free attempt head when safely known.</param>
/// <param name="Detail">The bounded non-sensitive explanation.</param>
public sealed record GovernedLoopEffectAttemptExecutionResult(
    GovernedLoopEffectAttemptExecutionStatus Status,
    GovernedLoopEffectAttempt? Attempt,
    string Detail);
