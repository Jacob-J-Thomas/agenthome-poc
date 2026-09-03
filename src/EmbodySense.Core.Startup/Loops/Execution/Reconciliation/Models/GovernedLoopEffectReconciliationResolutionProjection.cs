namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one immutable accepted resolution without authority evidence or caller text.</summary>
/// <param name="ResolutionId">The stable resolution identity.</param>
/// <param name="AssessmentHash">The accepted assessment hash.</param>
/// <param name="DispositionHash">The accepted disposition hash.</param>
/// <param name="Outcome">The typed reconciled outcome.</param>
/// <param name="OutcomeEvidenceId">The optional value-free outcome evidence identity.</param>
/// <param name="OutcomeEvidenceHash">The optional exact outcome evidence hash.</param>
/// <param name="ResolvedAtUtc">The trusted resolution time.</param>
/// <param name="ContentHash">The exact resolution content hash.</param>
public sealed record GovernedLoopEffectReconciliationResolutionProjection(
    string ResolutionId,
    string AssessmentHash,
    string DispositionHash,
    GovernedLoopEffectReconciliationResolutionOutcome Outcome,
    string? OutcomeEvidenceId,
    string? OutcomeEvidenceHash,
    DateTimeOffset ResolvedAtUtc,
    string ContentHash)
{

    /// <summary>Gets the stable resolution identity.</summary>
    public string ResolutionId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(ResolutionId, nameof(ResolutionId));
    /// <summary>Gets the accepted assessment hash.</summary>
    public string AssessmentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(AssessmentHash, nameof(AssessmentHash));
    /// <summary>Gets the accepted disposition hash.</summary>
    public string DispositionHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(DispositionHash, nameof(DispositionHash));
    /// <summary>Gets the typed reconciled outcome.</summary>
    public GovernedLoopEffectReconciliationResolutionOutcome Outcome { get; } = Outcome != GovernedLoopEffectReconciliationResolutionOutcome.Unknown && Enum.IsDefined(Outcome)
        ? Outcome
        : throw new ArgumentOutOfRangeException(nameof(Outcome));
    /// <summary>Gets the optional value-free outcome evidence identity.</summary>
    public string? OutcomeEvidenceId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalIdentifier(OutcomeEvidenceId, nameof(OutcomeEvidenceId));
    /// <summary>Gets the optional exact outcome evidence hash.</summary>
    public string? OutcomeEvidenceHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalHash(OutcomeEvidenceHash, nameof(OutcomeEvidenceHash));
    /// <summary>Gets the trusted resolution time.</summary>
    public DateTimeOffset ResolvedAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Utc(ResolvedAtUtc, nameof(ResolvedAtUtc));
    /// <summary>Gets the exact resolution content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContentHash, nameof(ContentHash));
}
