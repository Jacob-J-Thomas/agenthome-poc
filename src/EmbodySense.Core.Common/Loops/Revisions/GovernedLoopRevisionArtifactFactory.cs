using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Creates validated immutable, payload-agnostic governed-loop revision artifacts.</summary>
public static class GovernedLoopRevisionArtifactFactory
{
    /// <summary>Creates one validated immutable revision artifact.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="revision">The exact new revision identity.</param>
    /// <param name="predecessorRevision">The exact predecessor, or <see langword="null"/> for the first draft.</param>
    /// <param name="rollbackSourcePublication">The exact historical publication copied by rollback, or <see langword="null"/> for ordinary lineage.</param>
    /// <param name="creationOperationId">The globally idempotent creation operation.</param>
    /// <param name="createdByActorId">The authenticated actor recorded for creation.</param>
    /// <param name="createdAtUtc">The trusted UTC creation time.</param>
    /// <returns>A validated immutable artifact.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="revision"/> or the revision nested in a supplied rollback publication pin is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the schema, identities, lineage, hashes, or timestamp are invalid.</exception>
    public static GovernedLoopRevisionArtifact Create(
        int schemaVersion,
        GovernedLoopRevisionReference revision,
        GovernedLoopRevisionReference? predecessorRevision,
        GovernedLoopRevisionPublicationPin? rollbackSourcePublication,
        string creationOperationId,
        string createdByActorId,
        DateTimeOffset createdAtUtc)
    {
        var artifact = new GovernedLoopRevisionArtifact(
            schemaVersion,
            GovernedLoopRevisionContractGuard.CopyRevision(revision, nameof(revision)),
            GovernedLoopRevisionContractGuard.CopyOptionalRevision(predecessorRevision, nameof(predecessorRevision)),
            GovernedLoopRevisionContractGuard.CopyOptionalPin(rollbackSourcePublication, nameof(rollbackSourcePublication)),
            creationOperationId,
            createdByActorId,
            createdAtUtc);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(artifact), nameof(revision));
        return artifact;
    }
}
