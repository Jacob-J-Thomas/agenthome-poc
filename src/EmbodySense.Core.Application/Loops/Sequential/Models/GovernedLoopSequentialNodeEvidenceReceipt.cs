using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Projects the bounded causal coordinates of already-retained sequential node evidence.</summary>
/// <param name="SchemaVersion">The receipt schema version, which must be 1.</param>
/// <param name="Kind">The closed evidence-kind discriminator.</param>
/// <param name="WorkspaceId">The exact admitted workspace identity.</param>
/// <param name="RunId">The exact server-owned run identity.</param>
/// <param name="Revision">The exact immutable executable revision.</param>
/// <param name="ExecutionGeneration">The exact server-owned run generation.</param>
/// <param name="NodeId">The exact builder-selected node identity.</param>
/// <param name="Attempt">The positive bounded node attempt.</param>
/// <param name="Disposition">The exact handler disposition proved by the evidence.</param>
/// <param name="EvidenceHash">The canonical hash over every preceding field.</param>
public sealed record GovernedLoopSequentialNodeEvidenceReceipt(
    int SchemaVersion,
    GovernedLoopSequentialNodeEvidenceKind Kind,
    string WorkspaceId,
    string RunId,
    GovernedLoopRevisionReference Revision,
    long ExecutionGeneration,
    string NodeId,
    int Attempt,
    GovernedLoopSequentialNodeHandlerResultStatus Disposition,
    string EvidenceHash)
{
    /// <summary>Gets the only supported experimental receipt schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
