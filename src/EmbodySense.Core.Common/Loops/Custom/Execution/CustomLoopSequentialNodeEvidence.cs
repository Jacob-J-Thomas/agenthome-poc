using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>Retains exact bounded canonical-node dispatch or outcome evidence in the authoritative custom-run event stream.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="Kind">The closed evidence kind.</param>
/// <param name="WorkspaceId">The exact admitted workspace identity.</param>
/// <param name="RunId">The exact server-owned run identity.</param>
/// <param name="Revision">The exact immutable executable revision.</param>
/// <param name="ExecutionGeneration">The exact server-owned run generation.</param>
/// <param name="NodeId">The exact canonical graph-node identity.</param>
/// <param name="Attempt">The positive node-attempt identity, incremented for every repeated node visit.</param>
/// <param name="Disposition">The terminal disposition, or Unknown only for a dispatch-start marker.</param>
/// <param name="OutcomeArtifactHash">The exact hash of the containing durable event with its evidence field cleared.</param>
/// <param name="EvidenceHash">The canonical hash over every preceding field.</param>
public sealed record CustomLoopSequentialNodeEvidence(
    int SchemaVersion,
    CustomLoopSequentialNodeEvidenceKind Kind,
    string WorkspaceId,
    string RunId,
    GovernedLoopRevisionReference Revision,
    long ExecutionGeneration,
    string NodeId,
    int Attempt,
    CustomLoopSequentialNodeDisposition Disposition,
    string OutcomeArtifactHash,
    string EvidenceHash)
{
    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
