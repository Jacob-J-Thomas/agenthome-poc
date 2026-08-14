namespace EmbodySense.Core.Common.Loops.Execution;

internal static class GovernedLoopFrontierContractCopy
{
    internal static GovernedLoopNodeExecutionEvidence Copy(GovernedLoopNodeExecutionEvidence node)
        => GovernedLoopNodeExecutionEvidence.Create(node.PlanOrdinal, node.NodeId, node.Descriptor, node.IncomingControlEdgeIds, node.OutgoingControlEdgeIds, node.Status, node.Attempt, node.AttemptOperationId, node.OutcomeEvidenceId, node.OutcomeEvidenceHash);

    internal static GovernedLoopFrontierPayload Copy(GovernedLoopFrontierPayload payload)
    {
        return GovernedLoopFrontierPayload.Create(payload.SchemaVersion, payload.FrontierVersion, payload.ConcurrencyCeiling, payload.Status, payload.Nodes, payload.UpdatedAtUtc, payload.ContentHash);
    }
}
