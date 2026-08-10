using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Creates validated bounded governed-loop revision lifecycle operation evidence.</summary>
public static class GovernedLoopRevisionOperationEvidenceFactory
{
    /// <summary>Creates one validated immutable operation-evidence record.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="operationId">The globally idempotent operation identifier.</param>
    /// <param name="actorId">The authenticated actor recorded for the operation.</param>
    /// <param name="requestHash">The canonical request hash bound to the operation.</param>
    /// <param name="kind">The closed lifecycle operation.</param>
    /// <param name="outcome">The closed durable outcome.</param>
    /// <param name="failureCode">The closed durable failure cause, or none for a commit.</param>
    /// <param name="previousHead">The exact observed previous head, when one exists.</param>
    /// <param name="resultHead">The exact resulting or observed head, when durably known.</param>
    /// <param name="candidateRevision">The exact proposed successor revision, when applicable.</param>
    /// <param name="targetRevision">The exact existing target revision, when applicable.</param>
    /// <param name="rollbackSourcePublication">The exact historical publication selected by rollback.</param>
    /// <param name="authorityEvidenceHash">The server-produced mutation-authority evidence digest.</param>
    /// <param name="publicationValidationEvidenceHash">The publication-validation digest required by publish and rollback.</param>
    /// <param name="recordedAtUtc">The trusted UTC evidence time.</param>
    /// <returns>A validated immutable operation-evidence record.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a non-null nested lifecycle head or publication pin has a <see langword="null"/> exact revision.</exception>
    /// <exception cref="ArgumentException">Thrown when any identity, hash, enum, exact head, operation shape, outcome, lineage, or timestamp is invalid.</exception>
    public static GovernedLoopRevisionOperationEvidence Create(
        int schemaVersion,
        string operationId,
        string actorId,
        string requestHash,
        GovernedLoopRevisionOperationKind kind,
        GovernedLoopRevisionOperationOutcome outcome,
        GovernedLoopRevisionOperationFailureCode failureCode,
        GovernedLoopRevisionLifecycleHead? previousHead,
        GovernedLoopRevisionLifecycleHead? resultHead,
        GovernedLoopRevisionReference? candidateRevision,
        GovernedLoopRevisionReference? targetRevision,
        GovernedLoopRevisionPublicationPin? rollbackSourcePublication,
        string authorityEvidenceHash,
        string? publicationValidationEvidenceHash,
        DateTimeOffset recordedAtUtc)
    {
        var evidence = new GovernedLoopRevisionOperationEvidence(
            schemaVersion,
            operationId,
            actorId,
            requestHash,
            kind,
            outcome,
            failureCode,
            GovernedLoopRevisionContractGuard.CopyOptionalHead(previousHead, nameof(previousHead)),
            GovernedLoopRevisionContractGuard.CopyOptionalHead(resultHead, nameof(resultHead)),
            GovernedLoopRevisionContractGuard.CopyOptionalRevision(candidateRevision, nameof(candidateRevision)),
            GovernedLoopRevisionContractGuard.CopyOptionalRevision(targetRevision, nameof(targetRevision)),
            GovernedLoopRevisionContractGuard.CopyOptionalPin(rollbackSourcePublication, nameof(rollbackSourcePublication)),
            authorityEvidenceHash,
            publicationValidationEvidenceHash,
            recordedAtUtc);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(evidence), nameof(operationId));
        return evidence;
    }
}
