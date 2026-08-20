namespace EmbodySense.Core.Common.Loops.Execution;

internal static class GovernedLoopFrontierContractCopy
{
    internal static GovernedLoopNodeExecutionEvidence Copy(GovernedLoopNodeExecutionEvidence node)
        => GovernedLoopNodeExecutionEvidence.CreateActivation(
            node.ActivationOrdinal,
            node.PlanOrdinal,
            node.VisitOrdinal,
            node.NodeId,
            node.Descriptor,
            node.IncomingControlEdgeIds,
            node.OutgoingControlEdgeIds,
            node.Status,
            node.Attempt,
            node.AttemptOperationId,
            node.OutcomeEvidenceId,
            node.OutcomeEvidenceHash,
            node.CycleId,
            node.CycleIteration,
            node.ControlOutcome,
            node.SelectedControlEdgeIds,
            node.SkippedControlEdgeIds,
            node.JoinArrivals);

    internal static GovernedLoopFrontierPayload Copy(GovernedLoopFrontierPayload payload)
    {
        return GovernedLoopFrontierPayload.Create(payload.SchemaVersion, payload.FrontierVersion, payload.ConcurrencyCeiling, payload.Status, payload.Nodes, payload.UpdatedAtUtc, payload.ContentHash);
    }
}
