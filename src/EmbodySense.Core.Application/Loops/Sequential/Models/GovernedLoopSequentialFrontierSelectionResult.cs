namespace EmbodySense.Core.Application.Loops.Sequential.Models;

using EmbodySense.Core.Common.Loops.Execution;

/// <summary>Returns one deterministic frontier selection without granting authority to dispatch it.</summary>
/// <param name="Status">The selection posture.</param>
/// <param name="Node">The exact admitted plan node, when one is ready or running.</param>
/// <param name="Activation">The exact immutable durable activation, when one is ready or running.</param>
/// <param name="Attempt">The committed positive attempt for a running node, otherwise <see langword="null"/>.</param>
/// <param name="AttemptOperationId">The committed attempt operation identity for a running node, otherwise <see langword="null"/>.</param>
/// <param name="Detail">A bounded human-readable explanation.</param>
public sealed record GovernedLoopSequentialFrontierSelectionResult(
    GovernedLoopSequentialFrontierSelectionStatus Status,
    GovernedLoopSequentialPlanNode? Node,
    GovernedLoopNodeExecutionEvidence? Activation,
    int? Attempt,
    string? AttemptOperationId,
    string Detail);
