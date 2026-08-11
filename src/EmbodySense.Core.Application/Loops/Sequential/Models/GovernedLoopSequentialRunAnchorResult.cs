namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Reports one exact run-anchor guard decision.</summary>
/// <param name="Status">The closed guard disposition.</param>
/// <param name="Anchor">The guard-issued anchor only when ready.</param>
public sealed record GovernedLoopSequentialRunAnchorResult(
    GovernedLoopSequentialRunAnchorStatus Status,
    GovernedLoopSequentialRunAnchor? Anchor);
