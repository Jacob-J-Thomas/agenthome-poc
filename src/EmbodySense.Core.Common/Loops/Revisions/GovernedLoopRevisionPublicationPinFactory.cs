using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Creates validated exact governed-loop publication pins.</summary>
public static class GovernedLoopRevisionPublicationPinFactory
{
    /// <summary>Creates one validated exact publication pin.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="revision">The exact immutable published revision.</param>
    /// <param name="publicationOperationId">The idempotent publication operation.</param>
    /// <param name="validationEvidenceHash">The lowercase SHA-256 validation-evidence digest.</param>
    /// <returns>A validated exact publication pin.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="revision"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the schema, revision, operation identifier, or hash is invalid.</exception>
    public static GovernedLoopRevisionPublicationPin Create(int schemaVersion, GovernedLoopRevisionReference revision, string publicationOperationId, string validationEvidenceHash)
    {
        var pin = new GovernedLoopRevisionPublicationPin(
            schemaVersion,
            GovernedLoopRevisionContractGuard.CopyRevision(revision, nameof(revision)),
            publicationOperationId,
            validationEvidenceHash);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(pin), nameof(revision));
        return pin;
    }
}
