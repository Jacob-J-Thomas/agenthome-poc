namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Contains reusable unbound committed frontier evidence for one graph execution generation.</summary>
/// <remarks>Construction validates the aggregate posture and preserves sorted, unique node evidence.</remarks>
public sealed record GovernedLoopFrontierPayload
{
    private GovernedLoopFrontierPayload(int schemaVersion, long frontierVersion, GovernedLoopFrontierStatus status, IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes, DateTimeOffset updatedAtUtc)
    {
        SchemaVersion = schemaVersion;
        FrontierVersion = frontierVersion;
        Status = status;
        Nodes = nodes;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the positive optimistic frontier version.</summary>
    public long FrontierVersion { get; }

    /// <summary>Gets the aggregate frontier posture.</summary>
    public GovernedLoopFrontierStatus Status { get; }

    /// <summary>Gets the sorted unique bounded node evidence.</summary>
    public IReadOnlyList<GovernedLoopNodeExecutionEvidence> Nodes { get; }

    /// <summary>Gets the UTC timestamp of this committed frontier version.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Creates validated reusable unbound frontier evidence.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="frontierVersion">The positive bounded optimistic version.</param>
    /// <param name="status">The aggregate frontier posture.</param>
    /// <param name="nodes">The node evidence sorted by node identity.</param>
    /// <param name="updatedAtUtc">The UTC timestamp of this version.</param>
    /// <returns>The validated frontier evidence.</returns>
    public static GovernedLoopFrontierPayload Create(int schemaVersion, long frontierVersion, GovernedLoopFrontierStatus status, IEnumerable<GovernedLoopNodeExecutionEvidence> nodes, DateTimeOffset updatedAtUtc)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        if (!GovernedLoopExecutionStateMatrix.IsSupported(status))
        {
            throw new ArgumentException("A supported governed-loop frontier status is required.", nameof(status));
        }

        var snapshot = GovernedLoopExecutionContractGuard.SnapshotBounded(nodes, nameof(nodes), GovernedLoopExecutionLimits.MaxFrontierNodes);
        RequireSortedUniqueNodes(snapshot);
        if (!GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(status, snapshot))
        {
            throw new ArgumentException("Node evidence does not match the aggregate frontier status.", nameof(nodes));
        }

        return new GovernedLoopFrontierPayload(
            schemaVersion,
            GovernedLoopExecutionContractGuard.RequirePositiveVersion(frontierVersion, nameof(frontierVersion)),
            status,
            snapshot,
            GovernedLoopExecutionContractGuard.RequireUtc(updatedAtUtc, nameof(updatedAtUtc)));
    }

    private static void RequireSortedUniqueNodes(IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes)
    {
        for (var index = 1; index < nodes.Count; index++)
        {
            if (string.CompareOrdinal(nodes[index - 1].NodeId, nodes[index].NodeId) >= 0)
            {
                throw new ArgumentException("Governed-loop frontier nodes must be sorted and unique by node identity.", nameof(nodes));
            }
        }
    }
}
