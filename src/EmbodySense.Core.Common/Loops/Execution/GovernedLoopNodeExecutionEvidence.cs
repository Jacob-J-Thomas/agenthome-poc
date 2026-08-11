using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Records the immutable plan coordinates and committed posture of one canonical graph node.</summary>
public sealed record GovernedLoopNodeExecutionEvidence
{
    private GovernedLoopNodeExecutionEvidence(
        int planOrdinal,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        IReadOnlyList<string> incomingControlEdgeIds,
        IReadOnlyList<string> outgoingControlEdgeIds,
        GovernedLoopNodeExecutionStatus status,
        int? attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash)
    {
        SchemaVersion = CurrentSchemaVersion;
        PlanOrdinal = planOrdinal;
        NodeId = nodeId;
        Descriptor = descriptor with { };
        IncomingControlEdgeIds = Array.AsReadOnly(incomingControlEdgeIds.ToArray());
        OutgoingControlEdgeIds = Array.AsReadOnly(outgoingControlEdgeIds.ToArray());
        Status = status;
        Attempt = attempt;
        AttemptOperationId = attemptOperationId;
        OutcomeEvidenceId = outcomeEvidenceId;
        OutcomeEvidenceHash = outcomeEvidenceHash;
    }

    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopExecutionLimits.CurrentSchemaVersion;

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the zero-based deterministic execution-plan ordinal.</summary>
    public int PlanOrdinal { get; }

    /// <summary>Gets the exact graph-node identity.</summary>
    public string NodeId { get; }

    /// <summary>Gets a defensive copy of the exact admitted node descriptor.</summary>
    public GovernedLoopNodeDescriptor Descriptor { get; }

    /// <summary>Gets the sorted unique incoming control-edge identities.</summary>
    public IReadOnlyList<string> IncomingControlEdgeIds { get; }

    /// <summary>Gets the sorted unique outgoing control-edge identities.</summary>
    public IReadOnlyList<string> OutgoingControlEdgeIds { get; }

    /// <summary>Gets the committed node posture.</summary>
    public GovernedLoopNodeExecutionStatus Status { get; }

    /// <summary>Gets the positive attempt for selected nodes.</summary>
    public int? Attempt { get; }

    /// <summary>Gets the durable operation correlation for selected nodes.</summary>
    public string? AttemptOperationId { get; }

    /// <summary>Gets the retained outcome identity for committed terminal node outcomes.</summary>
    public string? OutcomeEvidenceId { get; }

    /// <summary>Gets the exact hash of retained terminal outcome evidence.</summary>
    public string? OutcomeEvidenceHash { get; }

    /// <summary>Creates validated schema-1 node evidence.</summary>
    public static GovernedLoopNodeExecutionEvidence Create(
        int planOrdinal,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        IEnumerable<string> incomingControlEdgeIds,
        IEnumerable<string> outgoingControlEdgeIds,
        GovernedLoopNodeExecutionStatus status,
        int? attempt = null,
        string? attemptOperationId = null,
        string? outcomeEvidenceId = null,
        string? outcomeEvidenceHash = null)
    {
        GovernedLoopExecutionContractGuard.RequirePlanOrdinal(planOrdinal, nameof(planOrdinal));
        GovernedLoopExecutionContractGuard.RequireNodeDescriptor(descriptor, nameof(descriptor));
        var incoming = GovernedLoopExecutionContractGuard.SnapshotSortedUniqueIdentifiers(incomingControlEdgeIds, nameof(incomingControlEdgeIds), GovernedLoopExecutionLimits.MaxIncomingEdges);
        var outgoing = GovernedLoopExecutionContractGuard.SnapshotSortedUniqueIdentifiers(outgoingControlEdgeIds, nameof(outgoingControlEdgeIds), GovernedLoopExecutionLimits.MaxOutgoingEdges);
        var selectedAttempt = GovernedLoopExecutionContractGuard.RequireOptionalAttempt(attempt, nameof(attempt));
        var operationId = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(attemptOperationId, nameof(attemptOperationId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        var evidenceId = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(outcomeEvidenceId, nameof(outcomeEvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        var evidenceHash = outcomeEvidenceHash is null ? null : GovernedLoopExecutionContractGuard.RequireSha256(outcomeEvidenceHash, nameof(outcomeEvidenceHash));
        if (!GovernedLoopExecutionStateMatrix.IsNodeEvidenceShapeValid(status, selectedAttempt, operationId is not null, evidenceId is not null, evidenceHash is not null))
        {
            throw new ArgumentException("Node attempt, operation, and outcome evidence do not match the node execution status.", nameof(status));
        }

        return new GovernedLoopNodeExecutionEvidence(
            planOrdinal,
            GovernedLoopExecutionContractGuard.RequireIdentifier(nodeId, nameof(nodeId)),
            descriptor,
            incoming,
            outgoing,
            status,
            selectedAttempt,
            operationId,
            evidenceId,
            evidenceHash);
    }
}
