using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

/// <summary>Returns one bounded workspace Action outcome without adapter details.</summary>
/// <param name="Status">The closed execution posture.</param>
/// <param name="CanonicalOutput">The exact value-free canonical result JSON for a completed outcome.</param>
/// <param name="Detail">The bounded non-sensitive explanation.</param>
/// <param name="PreparedEffectAttempt">The exact pre-dispatch retained effect when the executor requires governed Human Review.</param>
public sealed record GovernedLoopWorkspaceActionExecutionResult(
    GovernedLoopWorkspaceActionExecutionStatus Status,
    string? CanonicalOutput,
    string Detail,
    GovernedLoopEffectAttempt? PreparedEffectAttempt = null);
