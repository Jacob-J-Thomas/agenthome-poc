namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Reports one deterministic plan-build decision without retaining graph values in diagnostics.</summary>
/// <param name="Status">The closed build disposition.</param>
/// <param name="Plan">The builder-issued plan when ready; otherwise, <see langword="null"/>.</param>
/// <param name="FailurePath">The bounded value-free graph path that caused rejection, or <see langword="null"/> on success.</param>
public sealed record GovernedLoopSequentialPlanBuildResult(
    GovernedLoopSequentialPlanBuildStatus Status,
    GovernedLoopSequentialPlan? Plan,
    string? FailurePath);
