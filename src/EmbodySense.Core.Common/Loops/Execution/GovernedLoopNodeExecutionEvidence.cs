using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Records the immutable plan coordinates and committed posture of one canonical graph node.</summary>
public sealed record GovernedLoopNodeExecutionEvidence
{
    private GovernedLoopNodeExecutionEvidence(
        int activationOrdinal,
        int planOrdinal,
        int visitOrdinal,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        IReadOnlyList<string> incomingControlEdgeIds,
        IReadOnlyList<string> outgoingControlEdgeIds,
        string? cycleId,
        int? cycleIteration,
        GovernedLoopControlCondition? controlOutcome,
        IReadOnlyList<string> selectedControlEdgeIds,
        IReadOnlyList<string> skippedControlEdgeIds,
        IReadOnlyList<GovernedLoopJoinArrivalEvidence> joinArrivals,
        GovernedLoopNodeExecutionStatus status,
        int? attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash)
    {
        SchemaVersion = CurrentSchemaVersion;
        ActivationOrdinal = activationOrdinal;
        PlanOrdinal = planOrdinal;
        VisitOrdinal = visitOrdinal;
        NodeId = nodeId;
        Descriptor = descriptor with { };
        IncomingControlEdgeIds = Array.AsReadOnly(incomingControlEdgeIds.ToArray());
        OutgoingControlEdgeIds = Array.AsReadOnly(outgoingControlEdgeIds.ToArray());
        CycleId = cycleId;
        CycleIteration = cycleIteration;
        ControlOutcome = controlOutcome;
        SelectedControlEdgeIds = Array.AsReadOnly(selectedControlEdgeIds.ToArray());
        SkippedControlEdgeIds = Array.AsReadOnly(skippedControlEdgeIds.ToArray());
        JoinArrivals = Array.AsReadOnly(joinArrivals.Select(value => GovernedLoopJoinArrivalEvidence.Create(value.SchemaVersion, value.ControlEdgeId, value.SourceActivationOrdinal)).ToArray());
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

    /// <summary>Gets the zero-based ordinal of this exact activation in durable execution history.</summary>
    public int ActivationOrdinal { get; }

    /// <summary>Gets the zero-based deterministic admitted-plan ordinal, which remains stable across repeated visits.</summary>
    public int PlanOrdinal { get; }

    /// <summary>Gets the one-based visit ordinal for this exact graph-node identity.</summary>
    public int VisitOrdinal { get; }

    /// <summary>Gets the exact graph-node identity.</summary>
    public string NodeId { get; }

    /// <summary>Gets a defensive copy of the exact admitted node descriptor.</summary>
    public GovernedLoopNodeDescriptor Descriptor { get; }

    /// <summary>Gets the sorted unique incoming control-edge identities.</summary>
    public IReadOnlyList<string> IncomingControlEdgeIds { get; }

    /// <summary>Gets the sorted unique outgoing control-edge identities.</summary>
    public IReadOnlyList<string> OutgoingControlEdgeIds { get; }

    /// <summary>Gets the explicit cycle identity for a cyclic activation, or <see langword="null"/> for an acyclic activation.</summary>
    public string? CycleId { get; }

    /// <summary>Gets the positive cycle iteration paired with <see cref="CycleId"/>, or <see langword="null"/> for an acyclic activation.</summary>
    public int? CycleIteration { get; }

    /// <summary>Gets the exact committed control outcome, or <see langword="null"/> before routing is committed.</summary>
    /// <remarks>A skipped activation prunes its outgoing paths by posture and retains no control outcome or selected/skipped route partition of its own.</remarks>
    public GovernedLoopControlCondition? ControlOutcome { get; }

    /// <summary>Gets the sorted unique outgoing control edges selected by this exact activation.</summary>
    public IReadOnlyList<string> SelectedControlEdgeIds { get; }

    /// <summary>Gets the sorted unique outgoing control edges deterministically skipped by this exact activation.</summary>
    public IReadOnlyList<string> SkippedControlEdgeIds { get; }

    /// <summary>Gets the sorted immutable arrivals that made this exact join activation eligible.</summary>
    public IReadOnlyList<GovernedLoopJoinArrivalEvidence> JoinArrivals { get; }

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
        => CreateActivation(
            planOrdinal,
            planOrdinal,
            1,
            nodeId,
            descriptor,
            incomingControlEdgeIds,
            outgoingControlEdgeIds,
            status,
            attempt,
            attemptOperationId,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            null,
            null,
            null,
            [],
            [],
            []);

    /// <summary>Creates validated schema-1 evidence for one exact durable node activation.</summary>
    /// <param name="activationOrdinal">The zero-based activation-history ordinal.</param>
    /// <param name="planOrdinal">The zero-based immutable admitted-plan ordinal.</param>
    /// <param name="visitOrdinal">The positive visit ordinal for this node identity.</param>
    /// <param name="nodeId">The exact graph-node identity.</param>
    /// <param name="descriptor">The exact admitted node descriptor.</param>
    /// <param name="incomingControlEdgeIds">The sorted unique admitted incoming edge identities.</param>
    /// <param name="outgoingControlEdgeIds">The sorted unique admitted outgoing edge identities.</param>
    /// <param name="status">The committed activation posture.</param>
    /// <param name="attempt">The optional positive attempt within this visit.</param>
    /// <param name="attemptOperationId">The optional durable operation correlation.</param>
    /// <param name="outcomeEvidenceId">The optional retained terminal outcome identity.</param>
    /// <param name="outcomeEvidenceHash">The optional exact retained terminal outcome hash.</param>
    /// <param name="cycleId">The explicit cycle identity, paired with <paramref name="cycleIteration"/>.</param>
    /// <param name="cycleIteration">The positive explicit cycle iteration, paired with <paramref name="cycleId"/>.</param>
    /// <param name="controlOutcome">The exact committed routing outcome, or <see langword="null"/> before routing commits.</param>
    /// <param name="selectedControlEdgeIds">The sorted unique selected outgoing edges.</param>
    /// <param name="skippedControlEdgeIds">The sorted unique skipped outgoing edges.</param>
    /// <param name="joinArrivals">The sorted exact predecessor arrivals for a join activation.</param>
    /// <returns>The immutable activation evidence.</returns>
    public static GovernedLoopNodeExecutionEvidence CreateActivation(
        int activationOrdinal,
        int planOrdinal,
        int visitOrdinal,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        IEnumerable<string> incomingControlEdgeIds,
        IEnumerable<string> outgoingControlEdgeIds,
        GovernedLoopNodeExecutionStatus status,
        int? attempt = null,
        string? attemptOperationId = null,
        string? outcomeEvidenceId = null,
        string? outcomeEvidenceHash = null,
        string? cycleId = null,
        int? cycleIteration = null,
        GovernedLoopControlCondition? controlOutcome = null,
        IEnumerable<string>? selectedControlEdgeIds = null,
        IEnumerable<string>? skippedControlEdgeIds = null,
        IEnumerable<GovernedLoopJoinArrivalEvidence>? joinArrivals = null)
    {
        GovernedLoopExecutionContractGuard.RequireActivationOrdinal(activationOrdinal, nameof(activationOrdinal));
        GovernedLoopExecutionContractGuard.RequirePlanOrdinal(planOrdinal, nameof(planOrdinal));
        GovernedLoopExecutionContractGuard.RequireVisitOrdinal(visitOrdinal, nameof(visitOrdinal));
        GovernedLoopExecutionContractGuard.RequireNodeDescriptor(descriptor, nameof(descriptor));
        var incoming = GovernedLoopExecutionContractGuard.SnapshotSortedUniqueIdentifiers(incomingControlEdgeIds, nameof(incomingControlEdgeIds), GovernedLoopExecutionLimits.MaxIncomingEdges);
        var outgoing = GovernedLoopExecutionContractGuard.SnapshotSortedUniqueIdentifiers(outgoingControlEdgeIds, nameof(outgoingControlEdgeIds), GovernedLoopExecutionLimits.MaxOutgoingEdges);
        var selected = GovernedLoopExecutionContractGuard.SnapshotSortedUniqueIdentifiers(selectedControlEdgeIds ?? [], nameof(selectedControlEdgeIds), GovernedLoopExecutionLimits.MaxOutgoingEdges);
        var skipped = GovernedLoopExecutionContractGuard.SnapshotSortedUniqueIdentifiers(skippedControlEdgeIds ?? [], nameof(skippedControlEdgeIds), GovernedLoopExecutionLimits.MaxOutgoingEdges);
        var arrivals = GovernedLoopExecutionContractGuard.SnapshotJoinArrivals(joinArrivals ?? [], nameof(joinArrivals));
        var selectedCycleId = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(cycleId, nameof(cycleId));
        var selectedCycleIteration = GovernedLoopExecutionContractGuard.RequireOptionalCycleIteration(cycleIteration, nameof(cycleIteration));
        var selectedAttempt = GovernedLoopExecutionContractGuard.RequireOptionalAttempt(attempt, nameof(attempt));
        var operationId = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(attemptOperationId, nameof(attemptOperationId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        var evidenceId = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(outcomeEvidenceId, nameof(outcomeEvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        var evidenceHash = outcomeEvidenceHash is null ? null : GovernedLoopExecutionContractGuard.RequireSha256(outcomeEvidenceHash, nameof(outcomeEvidenceHash));
        if (!GovernedLoopExecutionStateMatrix.IsNodeEvidenceShapeValid(status, selectedAttempt, operationId is not null, evidenceId is not null, evidenceHash is not null))
        {
            throw new ArgumentException("Node attempt, operation, and outcome evidence do not match the node execution status.", nameof(status));
        }

        if ((selectedCycleId is null) != (selectedCycleIteration is null))
        {
            throw new ArgumentException("Cycle identity and positive iteration evidence must either both be present or both be absent.", nameof(cycleId));
        }

        if (controlOutcome is GovernedLoopControlCondition.Unknown || controlOutcome is { } suppliedOutcome && !Enum.IsDefined(suppliedOutcome))
        {
            throw new ArgumentException("A committed control outcome must be a supported non-unknown value.", nameof(controlOutcome));
        }

        if (controlOutcome is null && (selected.Count != 0 || skipped.Count != 0))
        {
            throw new ArgumentException("Selected or skipped routing evidence requires an exact committed control outcome.", nameof(controlOutcome));
        }

        if (controlOutcome is not null && status is not (GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed or GovernedLoopNodeExecutionStatus.ReviewBlocked or GovernedLoopNodeExecutionStatus.Skipped))
        {
            throw new ArgumentException("Control routing cannot commit before the activation reaches a terminal node posture.", nameof(controlOutcome));
        }

        if (status == GovernedLoopNodeExecutionStatus.Skipped && (controlOutcome is not null || selected.Count != 0 || skipped.Count != 0))
        {
            throw new ArgumentException("A skipped activation prunes its outgoing paths without inventing control-outcome or route-partition evidence.", nameof(controlOutcome));
        }

        if (selected.Intersect(skipped, StringComparer.Ordinal).Any()
            || selected.Concat(skipped).Except(outgoing, StringComparer.Ordinal).Any()
            || controlOutcome is not null && !selected.Concat(skipped).Order(StringComparer.Ordinal).SequenceEqual(outgoing, StringComparer.Ordinal))
        {
            throw new ArgumentException("Selected and skipped control edges must be disjoint exact subsets that partition the admitted outgoing edges after routing commits.", nameof(selectedControlEdgeIds));
        }

        if (arrivals.Count != 0
            && (descriptor.Kind != GovernedLoopNodeKind.Join || arrivals.Any(arrival => !incoming.Contains(arrival.ControlEdgeId, StringComparer.Ordinal))))
        {
            throw new ArgumentException("Join arrivals must identify exact admitted incoming edges of a join activation.", nameof(joinArrivals));
        }

        return new GovernedLoopNodeExecutionEvidence(
            activationOrdinal,
            planOrdinal,
            visitOrdinal,
            GovernedLoopExecutionContractGuard.RequireIdentifier(nodeId, nameof(nodeId)),
            descriptor,
            incoming,
            outgoing,
            selectedCycleId,
            selectedCycleIteration,
            controlOutcome,
            selected,
            skipped,
            arrivals,
            status,
            selectedAttempt,
            operationId,
            evidenceId,
            evidenceHash);
    }
}
