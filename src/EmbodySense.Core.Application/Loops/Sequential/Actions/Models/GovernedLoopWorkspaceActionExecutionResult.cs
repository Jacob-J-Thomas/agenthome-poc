namespace EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

/// <summary>Returns one bounded workspace Action outcome without adapter details.</summary>
/// <param name="Status">The closed execution posture.</param>
/// <param name="CanonicalOutput">The exact value-free canonical result JSON for a completed outcome.</param>
/// <param name="Detail">The bounded non-sensitive explanation.</param>
public sealed record GovernedLoopWorkspaceActionExecutionResult(
    GovernedLoopWorkspaceActionExecutionStatus Status,
    string? CanonicalOutput,
    string Detail);
