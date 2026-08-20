using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Projects the bounded causal coordinates of already-retained sequential node evidence.</summary>
/// <param name="SchemaVersion">The receipt schema version, which must be 1.</param>
/// <param name="Kind">The closed evidence-kind discriminator.</param>
/// <param name="WorkspaceId">The exact admitted workspace identity.</param>
/// <param name="RunId">The exact server-owned run identity.</param>
/// <param name="Revision">The exact immutable executable revision.</param>
/// <param name="ExecutionGeneration">The exact server-owned run generation.</param>
/// <param name="ActivationOrdinal">The zero-based durable activation-history identity.</param>
/// <param name="VisitOrdinal">The one-based visit identity for the canonical graph node.</param>
/// <param name="NodeId">The exact builder-selected node identity.</param>
/// <param name="Attempt">The positive bounded retry attempt within the activation.</param>
/// <param name="CycleId">The exact admitted cycle identity, or <see langword="null"/> for an acyclic activation.</param>
/// <param name="CycleIteration">The positive cycle iteration paired with <paramref name="CycleId"/>, or <see langword="null"/>.</param>
/// <param name="ControlOutcome">The exact committed control outcome, or <see langword="null"/> before routing is committed.</param>
/// <param name="SelectedControlEdgeIds">The sorted exact selected outgoing control edges.</param>
/// <param name="SkippedControlEdgeIds">The sorted exact skipped outgoing control edges.</param>
/// <param name="Disposition">The exact handler disposition proved by the evidence.</param>
/// <param name="OutcomeArtifactHash">The exact digest of the authenticated durable outcome event.</param>
/// <param name="EvidenceHash">The canonical hash over every preceding field.</param>
public sealed record GovernedLoopSequentialNodeEvidenceReceipt(
    int SchemaVersion,
    GovernedLoopSequentialNodeEvidenceKind Kind,
    string WorkspaceId,
    string RunId,
    GovernedLoopRevisionReference Revision,
    long ExecutionGeneration,
    int ActivationOrdinal,
    int VisitOrdinal,
    string NodeId,
    int Attempt,
    string? CycleId,
    int? CycleIteration,
    GovernedLoopControlCondition? ControlOutcome,
    string[] SelectedControlEdgeIds,
    string[] SkippedControlEdgeIds,
    GovernedLoopSequentialNodeHandlerResultStatus Disposition,
    string OutcomeArtifactHash,
    string EvidenceHash)
{
    /// <summary>Gets the only supported experimental receipt schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
