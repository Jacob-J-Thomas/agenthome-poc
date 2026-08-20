using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies one exact Ready activation that a committed route must prune before successors are exposed.</summary>
/// <param name="Activation">The exact immutable Ready activation.</param>
/// <param name="GoverningActivationOrdinal">The exact terminal activation committing the skipped edge.</param>
/// <param name="GoverningControlEdgeId">The exact skipped edge shared by the governing and pruned activations.</param>
public sealed record GovernedLoopSequentialPrunedActivation(
    GovernedLoopNodeExecutionEvidence Activation,
    int GoverningActivationOrdinal,
    string GoverningControlEdgeId);
