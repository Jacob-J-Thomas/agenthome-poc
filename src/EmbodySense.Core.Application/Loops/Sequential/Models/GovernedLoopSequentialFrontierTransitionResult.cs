using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns a pure canonical frontier transition without persisting or dispatching work.</summary>
/// <param name="Status">Whether the transition was applied.</param>
/// <param name="Frontier">The exact successor posture when applied.</param>
/// <param name="Detail">A bounded human-readable explanation.</param>
public sealed record GovernedLoopSequentialFrontierTransitionResult(
    GovernedLoopSequentialFrontierTransitionStatus Status,
    GovernedLoopFrontierPosture? Frontier,
    string Detail);
