using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Describes one immutable, payload-agnostic governed-loop revision artifact.</summary>
/// <param name="SchemaVersion">The artifact schema version.</param>
/// <param name="Revision">The exact graph, revision, and executable-content identity.</param>
/// <param name="PredecessorRevision">The exact prior lineage revision, or <see langword="null"/> for the first draft.</param>
/// <param name="RollbackSourcePublication">The exact historical publication copied into a rollback successor, or <see langword="null"/> for ordinary lineage.</param>
/// <param name="CreationOperationId">The globally idempotent operation that created the artifact.</param>
/// <param name="CreatedByActorId">The authenticated actor recorded for artifact creation.</param>
/// <param name="CreatedAtUtc">The trusted UTC creation time.</param>
public sealed record GovernedLoopRevisionArtifact(
    int SchemaVersion,
    GovernedLoopRevisionReference Revision,
    GovernedLoopRevisionReference? PredecessorRevision,
    GovernedLoopRevisionPublicationPin? RollbackSourcePublication,
    string CreationOperationId,
    string CreatedByActorId,
    DateTimeOffset CreatedAtUtc);
