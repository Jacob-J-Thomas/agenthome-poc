namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns the exact append-once skip evidence required before one route transition can commit.</summary>
/// <param name="Status">Whether the route and selected activation formed a valid pruning plan.</param>
/// <param name="Activations">The exact Ready activations that must receive skip evidence.</param>
/// <param name="Detail">A bounded diagnostic safe for runtime posture.</param>
public sealed record GovernedLoopSequentialPruningPlanResult(
    GovernedLoopSequentialFrontierTransitionStatus Status,
    IReadOnlyList<GovernedLoopSequentialPrunedActivation> Activations,
    string Detail);
