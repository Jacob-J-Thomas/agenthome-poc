using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Validates one exact bounded, hash-bound canonical execution frontier.</summary>
public static class GovernedLoopFrontierContractValidator
{
    /// <summary>Validates the complete bound frontier contract.</summary>
    public static GovernedLoopExecutionValidationResult Validate(GovernedLoopFrontierPosture? frontier)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (frontier is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$frontier");
            return GovernedLoopExecutionValidationResult.FromErrors(errors);
        }

        if (frontier.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion
            || frontier.Binding.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion
            || frontier.Payload.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion
            || frontier.Payload.Nodes.Any(node => node.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion
                || node.JoinArrivals.Any(arrival => arrival.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$frontier.schemaVersion");
        }

        if (frontier.Payload.ConcurrencyCeiling != GovernedLoopExecutionLimits.Schema1ConcurrencyCeiling
            || frontier.Payload.Nodes.Count(node => node.Status == GovernedLoopNodeExecutionStatus.Running) > frontier.Payload.ConcurrencyCeiling)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ConcurrencyCeilingExceeded, "$frontier.payload.concurrencyCeiling");
        }

        if (frontier.Payload.Nodes.Select((node, index) => node.ActivationOrdinal == index).Any(matches => !matches)
            || frontier.Payload.Nodes.GroupBy(node => node.NodeId, StringComparer.Ordinal).Any(group => group.Select((node, index) => node.VisitOrdinal == index + 1).Any(matches => !matches)))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.PlanPrefixInvalid, "$frontier.payload.nodes");
        }

        if (!GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(frontier.Payload.Status, frontier.Payload.Nodes))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.IllegalTransition, "$frontier.payload.status");
        }

        if (!GovernedLoopFrontierContractHash.Matches(frontier))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.IntegrityMismatch, "$frontier.payload.contentHash");
        }

        return GovernedLoopExecutionValidationResult.FromErrors(errors);
    }

    private static void Add(List<GovernedLoopExecutionValidationError> errors, GovernedLoopExecutionValidationErrorCode code, string path)
        => errors.Add(GovernedLoopExecutionValidationError.Create(code, path));
}
