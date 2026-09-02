namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

/// <summary>Crash-recovery journal for a reserved probe observation and case append.</summary>
internal sealed record GovernedLoopEffectReconciliationProbeJournal(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    string ReservationJson,
    string ObservationJson,
    GovernedLoopEffectReconciliationProbeJournalStage Stage,
    DateTimeOffset CreatedAtUtc,
    string ContentHash);
