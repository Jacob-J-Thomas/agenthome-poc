using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Contains one immutable, bounded canonical execution-frontier version.</summary>
public sealed record GovernedLoopFrontierPayload
{
    private GovernedLoopFrontierPayload(long frontierVersion, int concurrencyCeiling, GovernedLoopFrontierStatus status, IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes, DateTimeOffset updatedAtUtc, string contentHash)
    {
        SchemaVersion = CurrentSchemaVersion;
        FrontierVersion = frontierVersion;
        ConcurrencyCeiling = concurrencyCeiling;
        Status = status;
        Nodes = Array.AsReadOnly(nodes.Select(GovernedLoopFrontierContractCopy.Copy).ToArray());
        UpdatedAtUtc = updatedAtUtc;
        ContentHash = contentHash;
    }

    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopExecutionLimits.CurrentSchemaVersion;

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the positive optimistic frontier version.</summary>
    public long FrontierVersion { get; }

    /// <summary>Gets the admitted concurrent-node ceiling; schema 1 requires one.</summary>
    public int ConcurrencyCeiling { get; }

    /// <summary>Gets the aggregate frontier posture.</summary>
    public GovernedLoopFrontierStatus Status { get; }

    /// <summary>Gets the immutable contiguous deterministic activation history.</summary>
    public IReadOnlyList<GovernedLoopNodeExecutionEvidence> Nodes { get; }

    /// <summary>Gets the UTC commit timestamp.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Gets the exact hash of the complete bound frontier posture, or empty before binding and hashing.</summary>
    public string ContentHash { get; }

    /// <summary>Creates an unhashed validated schema-1 frontier payload.</summary>
    public static GovernedLoopFrontierPayload Create(
        int schemaVersion,
        long frontierVersion,
        int concurrencyCeiling,
        GovernedLoopFrontierStatus status,
        IEnumerable<GovernedLoopNodeExecutionEvidence> nodes,
        DateTimeOffset updatedAtUtc,
        string contentHash)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        var snapshot = GovernedLoopExecutionContractGuard.SnapshotBounded(nodes, nameof(nodes), GovernedLoopExecutionLimits.MaxFrontierNodes);
        GovernedLoopExecutionContractGuard.RequireCanonicalActivationHistory(snapshot, nameof(nodes));
        if (concurrencyCeiling != GovernedLoopExecutionLimits.Schema1ConcurrencyCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrencyCeiling), "Schema-1 governed-loop execution requires a concurrency ceiling of one.");
        }

        if (!GovernedLoopExecutionStateMatrix.IsFrontierShapeValid(status, snapshot))
        {
            throw new ArgumentException("Node evidence does not match the aggregate frontier status.", nameof(nodes));
        }

        if (snapshot.Count(node => node.Status == GovernedLoopNodeExecutionStatus.Running) > concurrencyCeiling)
        {
            throw new ArgumentException("Running frontier activations exceed the admitted concurrency ceiling.", nameof(nodes));
        }

        if (!string.IsNullOrEmpty(contentHash))
        {
            GovernedLoopExecutionContractGuard.RequireSha256(contentHash, nameof(contentHash));
        }

        return new GovernedLoopFrontierPayload(GovernedLoopExecutionContractGuard.RequirePositiveVersion(frontierVersion, nameof(frontierVersion)), concurrencyCeiling, status, snapshot, GovernedLoopExecutionContractGuard.RequireUtc(updatedAtUtc, nameof(updatedAtUtc)), contentHash);
    }

    internal GovernedLoopFrontierPayload WithContentHash(string contentHash)
        => new(FrontierVersion, ConcurrencyCeiling, Status, Nodes, UpdatedAtUtc, GovernedLoopExecutionContractGuard.RequireSha256(contentHash, nameof(contentHash)));
}
