namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>Owns defensive route snapshots and value semantics for sequential node evidence.</summary>
public sealed partial record CustomLoopSequentialNodeEvidence
{
    private IReadOnlyList<string>? _selectedControlEdgeIds = SelectedControlEdgeIds is null
        ? null
        : Array.AsReadOnly(SelectedControlEdgeIds.ToArray());
    private IReadOnlyList<string>? _skippedControlEdgeIds = SkippedControlEdgeIds is null
        ? null
        : Array.AsReadOnly(SkippedControlEdgeIds.ToArray());

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
            && string.Equals(FailureEvidenceId, other.FailureEvidenceId, StringComparison.Ordinal)
            && string.Equals(FailureEvidenceHash, other.FailureEvidenceHash, StringComparison.Ordinal)
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
        hash.Add(FailureEvidenceId, StringComparer.Ordinal);
        hash.Add(FailureEvidenceHash, StringComparer.Ordinal);
        hash.Add(EvidenceHash, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
