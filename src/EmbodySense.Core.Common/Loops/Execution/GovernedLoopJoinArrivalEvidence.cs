namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Identifies one exact predecessor activation whose selected control edge arrived at a join activation.</summary>
public sealed record GovernedLoopJoinArrivalEvidence
{
    private GovernedLoopJoinArrivalEvidence(string controlEdgeId, int sourceActivationOrdinal)
    {
        SchemaVersion = CurrentSchemaVersion;
        ControlEdgeId = controlEdgeId;
        SourceActivationOrdinal = sourceActivationOrdinal;
    }

    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopExecutionLimits.CurrentSchemaVersion;

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the exact incoming control-edge identity.</summary>
    public string ControlEdgeId { get; }

    /// <summary>Gets the zero-based activation ordinal that selected the incoming edge.</summary>
    public int SourceActivationOrdinal { get; }

    /// <summary>Creates validated schema-1 join-arrival evidence.</summary>
    /// <param name="schemaVersion">The exact schema version.</param>
    /// <param name="controlEdgeId">The exact incoming control-edge identity.</param>
    /// <param name="sourceActivationOrdinal">The zero-based source activation ordinal.</param>
    /// <returns>The immutable join-arrival evidence.</returns>
    public static GovernedLoopJoinArrivalEvidence Create(int schemaVersion, string controlEdgeId, int sourceActivationOrdinal)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        return new GovernedLoopJoinArrivalEvidence(
            GovernedLoopExecutionContractGuard.RequireIdentifier(controlEdgeId, nameof(controlEdgeId)),
            GovernedLoopExecutionContractGuard.RequireActivationOrdinal(sourceActivationOrdinal, nameof(sourceActivationOrdinal)));
    }
}
