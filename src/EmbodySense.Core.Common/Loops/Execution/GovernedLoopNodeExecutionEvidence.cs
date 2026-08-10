namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Records bounded value-free evidence for one exact graph-node posture.</summary>
/// <remarks>Construction validates attempt and committed-outcome shape before retaining defensive incoming-edge evidence.</remarks>
public sealed record GovernedLoopNodeExecutionEvidence
{
    private GovernedLoopNodeExecutionEvidence(string nodeId, IReadOnlyList<string> incomingEdgeIds, int? attempt, GovernedLoopNodeExecutionStatus status, string? outcomeEvidenceId)
    {
        NodeId = nodeId;
        IncomingEdgeIds = incomingEdgeIds;
        Attempt = attempt;
        Status = status;
        OutcomeEvidenceId = outcomeEvidenceId;
    }

    /// <summary>Gets the exact node identity from the bound graph revision.</summary>
    public string NodeId { get; }

    /// <summary>Gets the sorted unique incoming control-edge identities committed for this node execution.</summary>
    public IReadOnlyList<string> IncomingEdgeIds { get; }

    /// <summary>Gets the positive node attempt, or <see langword="null"/> before selection or when skipped.</summary>
    public int? Attempt { get; }

    /// <summary>Gets the committed node execution posture.</summary>
    public GovernedLoopNodeExecutionStatus Status { get; }

    /// <summary>Gets the retained value-free outcome evidence identity for a committed terminal node outcome.</summary>
    public string? OutcomeEvidenceId { get; }

    /// <summary>Creates validated node execution evidence.</summary>
    /// <param name="nodeId">The exact node identity.</param>
    /// <param name="incomingEdgeIds">The sorted unique committed incoming edge identities.</param>
    /// <param name="attempt">The positive attempt for selected nodes, otherwise <see langword="null"/>.</param>
    /// <param name="status">The supported node posture.</param>
    /// <param name="outcomeEvidenceId">The retained outcome evidence identity when required.</param>
    /// <returns>The validated node execution evidence.</returns>
    public static GovernedLoopNodeExecutionEvidence Create(string nodeId, IEnumerable<string> incomingEdgeIds, int? attempt, GovernedLoopNodeExecutionStatus status, string? outcomeEvidenceId)
    {
        if (!GovernedLoopExecutionStateMatrix.IsSupported(status))
        {
            throw new ArgumentException("A supported governed-loop node execution status is required.", nameof(status));
        }

        var validatedAttempt = GovernedLoopExecutionContractGuard.RequireOptionalAttempt(attempt, nameof(attempt));
        var validatedOutcome = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(outcomeEvidenceId, nameof(outcomeEvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        if (!GovernedLoopExecutionStateMatrix.IsNodeEvidenceShapeValid(status, validatedAttempt, validatedOutcome is not null))
        {
            throw new ArgumentException("Node attempt and outcome evidence do not match the node execution status.", nameof(status));
        }

        return new GovernedLoopNodeExecutionEvidence(
            GovernedLoopExecutionContractGuard.RequireIdentifier(nodeId, nameof(nodeId)),
            GovernedLoopExecutionContractGuard.SnapshotSortedUniqueIdentifiers(incomingEdgeIds, nameof(incomingEdgeIds), GovernedLoopExecutionLimits.MaxIncomingEdges),
            validatedAttempt,
            status,
            validatedOutcome);
    }
}
