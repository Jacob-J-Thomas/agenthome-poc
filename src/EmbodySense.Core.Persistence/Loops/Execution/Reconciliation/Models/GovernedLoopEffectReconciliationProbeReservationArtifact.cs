namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

/// <summary>Immutable value-free durable probe reservation envelope.</summary>
internal sealed record GovernedLoopEffectReconciliationProbeReservationArtifact(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    string CaseId,
    long CaseVersion,
    string CaseContentHash,
    string BindingHash,
    string EffectContentHash,
    string EffectJson,
    string SourceJson,
    string ContractJson,
    string InputJson,
    DateTimeOffset ReservedAtUtc,
    string ContentHash);
