namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Records one immutable value-free observation from an exactly registered evidence source.</summary>
/// <param name="SchemaVersion">The observation schema, which must be 1.</param>
/// <param name="CaseId">The exact reconciliation case identity.</param>
/// <param name="BindingHash">The exact reconciliation binding hash.</param>
/// <param name="ObservationId">The stable observation identity.</param>
/// <param name="SourceId">The exact registered source identity.</param>
/// <param name="SourceRegistrationHash">The exact content hash of the source registration used.</param>
/// <param name="Kind">How the observation completed.</param>
/// <param name="ReliabilityPosture">The reliability posture inherited from the source registration.</param>
/// <param name="ObservedOutcome">The exact external-effect state, present only when meaningful.</param>
/// <param name="EvidenceReference">The optional bounded value-free evidence reference.</param>
/// <param name="EvidenceHash">The optional verified exact evidence hash.</param>
/// <param name="ObservedAtUtc">The optional trusted UTC instant represented by the observation.</param>
/// <param name="RecordedAtUtc">The trusted UTC instant at which the observation was durably recorded.</param>
/// <param name="SafeSummary">Optional bounded operator-safe context; it never proves an outcome.</param>
/// <param name="ContentHash">The canonical hash of this observation except this field.</param>
public sealed record GovernedLoopEffectReconciliationObservation(
    int SchemaVersion,
    string CaseId,
    string BindingHash,
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
    string? SafeSummary,
    string ContentHash);
