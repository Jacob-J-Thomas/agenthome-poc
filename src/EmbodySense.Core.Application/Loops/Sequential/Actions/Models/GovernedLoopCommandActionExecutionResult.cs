namespace EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

/// <summary>Returns one safe canonical command Action result or closed failure posture.</summary>
/// <param name="Status">The bounded execution posture.</param>
/// <param name="CanonicalOutput">The value-free canonical result when a conclusive outcome is retained.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record GovernedLoopCommandActionExecutionResult(
    GovernedLoopCommandActionExecutionStatus Status,
    string? CanonicalOutput,
    string Detail);
