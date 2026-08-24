using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>Retains exact bounded canonical-node dispatch or outcome evidence in the authoritative custom-run event stream.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="Kind">The closed evidence kind.</param>
/// <param name="WorkspaceId">The exact admitted workspace identity.</param>
/// <param name="RunId">The exact server-owned run identity.</param>
/// <param name="Revision">The exact immutable executable revision.</param>
/// <param name="ExecutionGeneration">The exact server-owned run generation.</param>
/// <param name="ActivationOrdinal">The zero-based durable activation-history identity.</param>
/// <param name="VisitOrdinal">The one-based visit identity for the canonical graph node.</param>
/// <param name="NodeId">The exact canonical graph-node identity.</param>
/// <param name="Attempt">The positive retry attempt within this exact activation, or <see langword="null"/> for an undispatched pruned activation.</param>
/// <param name="CycleId">The explicit admitted cycle identity, or <see langword="null"/> for an acyclic activation.</param>
/// <param name="CycleIteration">The positive cycle iteration paired with <paramref name="CycleId"/>, or <see langword="null"/>.</param>
/// <param name="ControlOutcome">The exact committed control outcome, or <see langword="null"/> before routing is committed.</param>
/// <param name="SelectedControlEdgeIds">The sorted exact outgoing control edges selected by this outcome.</param>
/// <param name="SkippedControlEdgeIds">The sorted exact outgoing control edges skipped by this outcome.</param>
/// <param name="GoverningActivationOrdinal">The exact earlier activation that pruned this activation, or <see langword="null"/> for non-skip evidence.</param>
/// <param name="GoverningControlEdgeId">The exact incoming edge pruned by that governing activation, or <see langword="null"/> for non-skip evidence.</param>
/// <param name="Disposition">The terminal disposition, or Unknown only for a dispatch-start marker.</param>
/// <param name="OutcomeArtifactHash">The exact hash of the containing durable event with its evidence field cleared.</param>
/// <param name="EvidenceHash">The canonical hash over every preceding field.</param>
public sealed partial record CustomLoopSequentialNodeEvidence(
    int SchemaVersion,
    CustomLoopSequentialNodeEvidenceKind Kind,
    string WorkspaceId,
    string RunId,
    GovernedLoopRevisionReference Revision,
    long ExecutionGeneration,
    int ActivationOrdinal,
    int VisitOrdinal,
    string NodeId,
    int? Attempt,
    string? CycleId,
    int? CycleIteration,
    GovernedLoopControlCondition? ControlOutcome,
    IReadOnlyList<string> SelectedControlEdgeIds,
    IReadOnlyList<string> SkippedControlEdgeIds,
    int? GoverningActivationOrdinal,
    string? GoverningControlEdgeId,
    CustomLoopSequentialNodeDisposition Disposition,
    string OutcomeArtifactHash,
    string EvidenceHash)
{
    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the exact classified failure evidence identity carried by a failed or review-blocked activation.</summary>
    [JsonRequired]
    public string? FailureEvidenceId { get; init; }

    /// <summary>Gets the exact classified failure evidence hash carried by a failed or review-blocked activation.</summary>
    [JsonRequired]
    public string? FailureEvidenceHash { get; init; }
}
