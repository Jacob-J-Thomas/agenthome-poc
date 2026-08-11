using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

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
public sealed record CustomLoopSequentialNodeEvidence(
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
    private IReadOnlyList<string>? _selectedControlEdgeIds = SelectedControlEdgeIds is null
        ? null
        : Array.AsReadOnly(SelectedControlEdgeIds.ToArray());
    private IReadOnlyList<string>? _skippedControlEdgeIds = SkippedControlEdgeIds is null
        ? null
        : Array.AsReadOnly(SkippedControlEdgeIds.ToArray());

    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets a defensive read-only snapshot of exact selected route identities.</summary>
    public IReadOnlyList<string> SelectedControlEdgeIds
    {
        get => _selectedControlEdgeIds!;
        init => _selectedControlEdgeIds = value is null ? null : Array.AsReadOnly(value.ToArray());
    }

    /// <summary>Gets a defensive read-only snapshot of exact skipped route identities.</summary>
    public IReadOnlyList<string> SkippedControlEdgeIds
    {
        get => _skippedControlEdgeIds!;
        init => _skippedControlEdgeIds = value is null ? null : Array.AsReadOnly(value.ToArray());
    }

    /// <summary>Compares every exact coordinate and route sequence by value so persistence reloads retain record semantics.</summary>
    public bool Equals(CustomLoopSequentialNodeEvidence? other)
        => ReferenceEquals(this, other)
            || other is not null
            && SchemaVersion == other.SchemaVersion
            && Kind == other.Kind
            && string.Equals(WorkspaceId, other.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(RunId, other.RunId, StringComparison.Ordinal)
            && Equals(Revision, other.Revision)
            && ExecutionGeneration == other.ExecutionGeneration
            && ActivationOrdinal == other.ActivationOrdinal
            && VisitOrdinal == other.VisitOrdinal
            && string.Equals(NodeId, other.NodeId, StringComparison.Ordinal)
            && Attempt == other.Attempt
            && string.Equals(CycleId, other.CycleId, StringComparison.Ordinal)
            && CycleIteration == other.CycleIteration
            && ControlOutcome == other.ControlOutcome
            && SelectedControlEdgeIds is not null
            && other.SelectedControlEdgeIds is not null
            && SelectedControlEdgeIds.SequenceEqual(other.SelectedControlEdgeIds, StringComparer.Ordinal)
            && SkippedControlEdgeIds is not null
            && other.SkippedControlEdgeIds is not null
            && SkippedControlEdgeIds.SequenceEqual(other.SkippedControlEdgeIds, StringComparer.Ordinal)
            && GoverningActivationOrdinal == other.GoverningActivationOrdinal
            && string.Equals(GoverningControlEdgeId, other.GoverningControlEdgeId, StringComparison.Ordinal)
            && Disposition == other.Disposition
            && string.Equals(OutcomeArtifactHash, other.OutcomeArtifactHash, StringComparison.Ordinal)
            && string.Equals(EvidenceHash, other.EvidenceHash, StringComparison.Ordinal);

    /// <summary>Hashes the same exact scalar and ordinal route values used by typed equality.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Kind);
        hash.Add(WorkspaceId, StringComparer.Ordinal);
        hash.Add(RunId, StringComparer.Ordinal);
        hash.Add(Revision);
        hash.Add(ExecutionGeneration);
        hash.Add(ActivationOrdinal);
        hash.Add(VisitOrdinal);
        hash.Add(NodeId, StringComparer.Ordinal);
        hash.Add(Attempt);
        hash.Add(CycleId, StringComparer.Ordinal);
        hash.Add(CycleIteration);
        hash.Add(ControlOutcome);
        foreach (var edgeId in SelectedControlEdgeIds ?? [])
        {
            hash.Add(edgeId, StringComparer.Ordinal);
        }

        hash.Add(SelectedControlEdgeIds?.Count ?? -1);
        foreach (var edgeId in SkippedControlEdgeIds ?? [])
        {
            hash.Add(edgeId, StringComparer.Ordinal);
        }

        hash.Add(SkippedControlEdgeIds?.Count ?? -1);
        hash.Add(GoverningActivationOrdinal);
        hash.Add(GoverningControlEdgeId, StringComparer.Ordinal);
        hash.Add(Disposition);
        hash.Add(OutcomeArtifactHash, StringComparer.Ordinal);
        hash.Add(EvidenceHash, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
