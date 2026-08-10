using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>
/// Describes typed legacy projection posture without claiming canonical optimistic versions or a revision-bound source fact.
/// </summary>
public sealed class GovernedLoopCompatibilityProjectionObservation
{
    internal GovernedLoopCompatibilityProjectionObservation(
        string projectionId,
        string operationId,
        GovernedLoopProjectionClass projectionClass,
        GovernedLoopProjectionStatus status,
        string sourceEvidenceId,
        string? effectId,
        DateTimeOffset observedAtUtc)
    {
        ProjectionId = GovernedLoopCompatibilityValueGuard.RequireSourceIdentifier(projectionId, nameof(projectionId));
        OperationId = GovernedLoopCompatibilityValueGuard.RequireSourceIdentifier(operationId, nameof(operationId));
        Class = GovernedLoopCompatibilityValueGuard.RequireConcrete(projectionClass, nameof(projectionClass));
        Status = GovernedLoopCompatibilityValueGuard.RequireConcrete(status, nameof(status));
        SourceEvidenceId = GovernedLoopCompatibilityValueGuard.RequireSourceIdentifier(sourceEvidenceId, nameof(sourceEvidenceId));
        EffectId = GovernedLoopCompatibilityValueGuard.RequireOptionalSourceIdentifier(effectId, nameof(effectId));
        var validWithoutVersions = GovernedLoopExecutionStateMatrix.IsProjectionStateValid(
            Class,
            Status,
            hasExpectedVersion: false,
            hasCommittedVersion: false,
            hasReconciliationEvidence: false);
        var explicitVersionGap = status is GovernedLoopProjectionStatus.Conflict or GovernedLoopProjectionStatus.ReconciliationRequired
            && projectionClass is GovernedLoopProjectionClass.DurableReadModel or GovernedLoopProjectionClass.Surface;
        if (!validWithoutVersions && !explicitVersionGap)
        {
            throw new ArgumentException("The compatibility projection class and status require canonical optimistic versions that the source does not retain.", nameof(status));
        }

        ObservedAtUtc = GovernedLoopCompatibilityValueGuard.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }

    /// <summary>Gets the stable projection identity retained or deterministically named by the source protocol.</summary>
    public string ProjectionId { get; }

    /// <summary>Gets the stable source operation identity.</summary>
    public string OperationId { get; }

    /// <summary>Gets the canonical projection class supported by the source behavior.</summary>
    public GovernedLoopProjectionClass Class { get; }

    /// <summary>Gets the source-supported projection posture.</summary>
    public GovernedLoopProjectionStatus Status { get; }

    /// <summary>Gets the exact typed source transition or event supporting the observation.</summary>
    public string SourceEvidenceId { get; }

    /// <summary>Gets the related compatibility effect identity when the source binds one.</summary>
    public string? EffectId { get; }

    /// <summary>Gets the source-owned UTC observation time.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

}
