using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Contains reusable unbound, value-free synchronization evidence for one derived projection.</summary>
/// <remarks>Construction validates class-specific optimistic and committed version semantics.</remarks>
public sealed record GovernedLoopProjectionPayload
{
    private GovernedLoopProjectionPayload(
        int schemaVersion,
        string projectionId,
        string operationId,
        GovernedLoopProjectionClass projectionClass,
        GovernedLoopProjectionStatus status,
        string sourceEvidenceId,
        string? effectId,
        string? expectedVersion,
        string? committedVersion,
        string? reconciliationEvidenceId,
        DateTimeOffset updatedAtUtc)
    {
        SchemaVersion = schemaVersion;
        ProjectionId = projectionId;
        OperationId = operationId;
        Class = projectionClass;
        Status = status;
        SourceEvidenceId = sourceEvidenceId;
        EffectId = effectId;
        ExpectedVersion = expectedVersion;
        CommittedVersion = committedVersion;
        ReconciliationEvidenceId = reconciliationEvidenceId;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable projection identity.</summary>
    public string ProjectionId { get; }

    /// <summary>Gets the stable idempotency operation identity.</summary>
    public string OperationId { get; }

    /// <summary>Gets the projection class.</summary>
    public GovernedLoopProjectionClass Class { get; }

    /// <summary>Gets the synchronization posture.</summary>
    public GovernedLoopProjectionStatus Status { get; }

    /// <summary>Gets the bound run identity or retained node/effect evidence identity from which this projection is derived, including while pending.</summary>
    public string SourceEvidenceId { get; }

    /// <summary>Gets the source effect identity when the projection follows an externally meaningful effect.</summary>
    public string? EffectId { get; }

    /// <summary>Gets the bounded optimistic-precondition version identity when one applies.</summary>
    public string? ExpectedVersion { get; }

    /// <summary>Gets the bounded committed projection version identity after successful synchronization.</summary>
    public string? CommittedVersion { get; }

    /// <summary>Gets the retained reconciliation or operator-disposition evidence identity after an explicit projection reconciliation.</summary>
    public string? ReconciliationEvidenceId { get; }

    /// <summary>Gets the UTC timestamp of this committed projection version.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Creates validated reusable unbound projection evidence.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="projectionId">The stable projection identity.</param>
    /// <param name="operationId">The stable idempotency operation identity.</param>
    /// <param name="projectionClass">The projection class.</param>
    /// <param name="status">The synchronization posture.</param>
    /// <param name="sourceEvidenceId">The bound run identity or retained source evidence identity.</param>
    /// <param name="effectId">The source effect identity when applicable.</param>
    /// <param name="expectedVersion">The optimistic-precondition version identity when applicable.</param>
    /// <param name="committedVersion">The committed version identity after successful synchronization.</param>
    /// <param name="reconciliationEvidenceId">The reconciliation or operator-disposition evidence identity for a reconciled projection.</param>
    /// <param name="updatedAtUtc">The UTC timestamp of this committed projection version.</param>
    /// <returns>The validated projection payload.</returns>
    /// <exception cref="ArgumentException">Thrown when the schema, identities, projection-state axes, evidence references, or timestamp is invalid.</exception>
    public static GovernedLoopProjectionPayload Create(
        int schemaVersion,
        string projectionId,
        string operationId,
        GovernedLoopProjectionClass projectionClass,
        GovernedLoopProjectionStatus status,
        string sourceEvidenceId,
        string? effectId,
        string? expectedVersion,
        string? committedVersion,
        string? reconciliationEvidenceId,
        DateTimeOffset updatedAtUtc)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        var reconciliationReference = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(
            reconciliationEvidenceId,
            nameof(reconciliationEvidenceId),
            GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        if (!GovernedLoopExecutionStateMatrix.IsProjectionStateValid(
                projectionClass,
                status,
                expectedVersion is not null,
                committedVersion is not null,
                reconciliationReference is not null))
        {
            throw new ArgumentException("Projection class, status, and version references form an illegal state.", nameof(status));
        }

        return new GovernedLoopProjectionPayload(
            schemaVersion,
            GovernedLoopExecutionContractGuard.RequireIdentifier(projectionId, nameof(projectionId)),
            GovernedLoopExecutionContractGuard.RequireIdentifier(operationId, nameof(operationId)),
            projectionClass,
            status,
            GovernedLoopExecutionContractGuard.RequireIdentifier(sourceEvidenceId, nameof(sourceEvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters),
            GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(effectId, nameof(effectId)),
            GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(expectedVersion, nameof(expectedVersion), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters),
            GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(committedVersion, nameof(committedVersion), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters),
            reconciliationReference,
            GovernedLoopExecutionContractGuard.RequireUtc(updatedAtUtc, nameof(updatedAtUtc)));
    }
}
