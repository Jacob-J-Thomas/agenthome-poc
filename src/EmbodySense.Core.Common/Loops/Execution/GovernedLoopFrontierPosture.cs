namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Binds one committed frontier to exact workspace, admission, graph, run, and execution-generation evidence.</summary>
public sealed record GovernedLoopFrontierPosture
{
    private GovernedLoopFrontierPosture(string workspaceId, GovernedLoopExecutionBinding binding, string graphArtifactHash, string graphLayoutHash, string admissionReceiptHash, GovernedLoopFrontierPayload payload)
    {
        SchemaVersion = CurrentSchemaVersion;
        WorkspaceId = workspaceId;
        Binding = GovernedLoopExecutionBinding.Create(binding.SchemaVersion, binding.RunId, binding.Revision, binding.ExecutionGeneration);
        GraphArtifactHash = graphArtifactHash;
        GraphLayoutHash = graphLayoutHash;
        AdmissionReceiptHash = admissionReceiptHash;
        Payload = GovernedLoopFrontierContractCopy.Copy(payload);
    }

    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopExecutionLimits.CurrentSchemaVersion;

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the exact canonical workspace scope.</summary>
    public string WorkspaceId { get; }

    /// <summary>Gets a defensive copy of the exact execution binding.</summary>
    public GovernedLoopExecutionBinding Binding { get; }

    /// <summary>Gets the exact immutable graph-artifact hash.</summary>
    public string GraphArtifactHash { get; }

    /// <summary>Gets the exact immutable graph-layout hash.</summary>
    public string GraphLayoutHash { get; }

    /// <summary>Gets the exact successful admission-receipt hash.</summary>
    public string AdmissionReceiptHash { get; }

    /// <summary>Gets a defensive copy of the committed frontier payload.</summary>
    public GovernedLoopFrontierPayload Payload { get; }

    /// <summary>Creates a validated unhashed schema-1 bound frontier.</summary>
    public static GovernedLoopFrontierPosture Create(GovernedLoopExecutionBinding binding, string workspaceId, string graphArtifactHash, string graphLayoutHash, string admissionReceiptHash, GovernedLoopFrontierPayload payload)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(payload);
        var frontier = new GovernedLoopFrontierPosture(
            GovernedLoopExecutionContractGuard.RequireWorkspaceId(workspaceId, nameof(workspaceId)),
            binding,
            GovernedLoopExecutionContractGuard.RequireSha256(graphArtifactHash, nameof(graphArtifactHash)),
            GovernedLoopExecutionContractGuard.RequireSha256(graphLayoutHash, nameof(graphLayoutHash)),
            GovernedLoopExecutionContractGuard.RequireSha256(admissionReceiptHash, nameof(admissionReceiptHash)),
            payload);
        if (string.IsNullOrEmpty(frontier.Payload.ContentHash))
        {
            return GovernedLoopFrontierContractHash.Apply(frontier);
        }

        if (!GovernedLoopFrontierContractHash.Matches(frontier))
        {
            throw new ArgumentException("Governed-loop frontier content hash does not match its exact bound posture.", nameof(payload));
        }

        return frontier;
    }

    /// <summary>Creates a validated schema-1 bound frontier from its canonical aggregate coordinates.</summary>
    /// <param name="binding">The exact execution binding.</param>
    /// <param name="workspaceId">The canonical workspace scope.</param>
    /// <param name="graphArtifactHash">The exact graph-artifact hash.</param>
    /// <param name="graphLayoutHash">The exact graph-layout hash.</param>
    /// <param name="admissionReceiptHash">The exact successful admission-receipt hash.</param>
    /// <param name="frontierVersion">The positive optimistic frontier version.</param>
    /// <param name="concurrencyCeiling">The admitted concurrent-node ceiling.</param>
    /// <param name="status">The aggregate frontier posture.</param>
    /// <param name="nodes">The immutable contiguous deterministic plan prefix.</param>
    /// <param name="updatedAtUtc">The UTC frontier commit timestamp.</param>
    /// <param name="contentHash">The exact retained frontier hash, or empty to compute it.</param>
    /// <returns>The validated, defensively copied canonical frontier posture.</returns>
    public static GovernedLoopFrontierPosture Create(
        GovernedLoopExecutionBinding binding,
        string workspaceId,
        string graphArtifactHash,
        string graphLayoutHash,
        string admissionReceiptHash,
        long frontierVersion,
        int concurrencyCeiling,
        Models.GovernedLoopFrontierStatus status,
        IEnumerable<GovernedLoopNodeExecutionEvidence> nodes,
        DateTimeOffset updatedAtUtc,
        string contentHash)
    {
        var payload = GovernedLoopFrontierPayload.Create(
            GovernedLoopFrontierPayload.CurrentSchemaVersion,
            frontierVersion,
            concurrencyCeiling,
            status,
            nodes,
            updatedAtUtc,
            contentHash);
        return Create(
            binding,
            workspaceId,
            graphArtifactHash,
            graphLayoutHash,
            admissionReceiptHash,
            payload);
    }

    internal GovernedLoopFrontierPosture WithPayload(GovernedLoopFrontierPayload payload)
        => new(WorkspaceId, Binding, GraphArtifactHash, GraphLayoutHash, AdmissionReceiptHash, payload);
}
