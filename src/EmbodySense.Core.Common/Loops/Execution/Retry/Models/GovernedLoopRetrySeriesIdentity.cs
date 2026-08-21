using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Execution.Retry.Models;

/// <summary>Binds one immutable retry series to the exact run revision, node visit, original failure, and deadline.</summary>
/// <param name="SchemaVersion">The identity schema version, which must be 1.</param>
/// <param name="SeriesId">The deterministic lowercase SHA-256 series identity.</param>
/// <param name="WorkspaceId">The exact admitted workspace identity.</param>
/// <param name="RunId">The exact server-owned run identity.</param>
/// <param name="Revision">The exact immutable executable revision.</param>
/// <param name="ExecutionGeneration">The exact server-owned execution generation.</param>
/// <param name="ActivationOrdinal">The exact zero-based frontier activation.</param>
/// <param name="VisitOrdinal">The exact positive node visit.</param>
/// <param name="NodeId">The exact graph-node identity.</param>
/// <param name="OriginatingFailureEvidenceId">The exact first classified failure identity.</param>
/// <param name="OriginatingFailureEvidenceHash">The exact first classified failure digest.</param>
/// <param name="PolicyId">The exact admitted policy identity.</param>
/// <param name="PolicyHash">The exact admitted policy digest.</param>
/// <param name="StartedAtUtc">The trusted UTC instant at which the series began.</param>
/// <param name="DeadlineUtc">The immutable earliest cumulative deadline.</param>
/// <param name="ContentHash">The canonical lowercase SHA-256 digest over every preceding field.</param>
public sealed record GovernedLoopRetrySeriesIdentity(
    int SchemaVersion,
    string SeriesId,
    string WorkspaceId,
    string RunId,
    GovernedLoopRevisionReference Revision,
    long ExecutionGeneration,
    int ActivationOrdinal,
    int VisitOrdinal,
    string NodeId,
    string OriginatingFailureEvidenceId,
    string OriginatingFailureEvidenceHash,
    string PolicyId,
    string PolicyHash,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental identity schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
