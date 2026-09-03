namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one immutable value-free reconciliation observation.</summary>
/// <param name="ObservationId">The stable observation identity.</param>
/// <param name="SourceId">The registered source identity.</param>
/// <param name="SourceRegistrationHash">The exact source registration hash.</param>
/// <param name="Kind">How the observation completed.</param>
/// <param name="ReliabilityPosture">The inherited reliability posture.</param>
/// <param name="ObservedOutcome">The typed observed outcome.</param>
/// <param name="EvidenceReference">The optional value-free evidence reference.</param>
/// <param name="EvidenceHash">The optional exact evidence hash.</param>
/// <param name="ObservedAtUtc">The optional trusted external observation time.</param>
/// <param name="RecordedAtUtc">The trusted durable record time.</param>
/// <param name="ContentHash">The exact observation content hash.</param>
public sealed record GovernedLoopEffectReconciliationObservationProjection(
    string ObservationId,
    string SourceId,
    string SourceRegistrationHash,
    GovernedLoopEffectReconciliationObservationKind Kind,
    GovernedLoopEffectReconciliationReliabilityPosture ReliabilityPosture,
    GovernedLoopEffectReconciliationObservedOutcome ObservedOutcome,
    string? EvidenceReference,
    string? EvidenceHash,
    DateTimeOffset? ObservedAtUtc,
    DateTimeOffset RecordedAtUtc,
    string ContentHash)
{

    /// <summary>Gets the stable observation identity.</summary>
    public string ObservationId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(ObservationId, nameof(ObservationId));
    /// <summary>Gets the registered source identity.</summary>
    public string SourceId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(SourceId, nameof(SourceId));
    /// <summary>Gets the exact source registration hash.</summary>
    public string SourceRegistrationHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(SourceRegistrationHash, nameof(SourceRegistrationHash));
    /// <summary>Gets how the observation completed.</summary>
    public GovernedLoopEffectReconciliationObservationKind Kind { get; } = Kind != GovernedLoopEffectReconciliationObservationKind.Unknown && Enum.IsDefined(Kind)
        ? Kind
        : throw new ArgumentOutOfRangeException(nameof(Kind));
    /// <summary>Gets the inherited reliability posture.</summary>
    public GovernedLoopEffectReconciliationReliabilityPosture ReliabilityPosture { get; } = ReliabilityPosture != GovernedLoopEffectReconciliationReliabilityPosture.Unknown && Enum.IsDefined(ReliabilityPosture)
        ? ReliabilityPosture
        : throw new ArgumentOutOfRangeException(nameof(ReliabilityPosture));
    /// <summary>Gets the typed observed outcome.</summary>
    public GovernedLoopEffectReconciliationObservedOutcome ObservedOutcome { get; } = Enum.IsDefined(ObservedOutcome)
        ? ObservedOutcome
        : throw new ArgumentOutOfRangeException(nameof(ObservedOutcome));
    /// <summary>Gets the optional value-free evidence reference.</summary>
    public string? EvidenceReference { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalIdentifier(EvidenceReference, nameof(EvidenceReference));
    /// <summary>Gets the optional exact evidence hash.</summary>
    public string? EvidenceHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalHash(EvidenceHash, nameof(EvidenceHash));
    /// <summary>Gets the optional trusted external observation time.</summary>
    public DateTimeOffset? ObservedAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalUtc(ObservedAtUtc, nameof(ObservedAtUtc));
    /// <summary>Gets the trusted durable record time.</summary>
    public DateTimeOffset RecordedAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Utc(RecordedAtUtc, nameof(RecordedAtUtc));
    /// <summary>Gets the exact observation content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContentHash, nameof(ContentHash));
}
