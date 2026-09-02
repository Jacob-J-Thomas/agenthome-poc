namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

internal sealed record GovernedLoopEffectReconciliationJournal(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    string Purpose,
    string CaseId,
    string StorageKey,
    string ReplacementJson,
    string? SuccessorJson,
    string ReplacementHash,
    long ReplacementVersion,
    string? ExpectedCaseHash,
    string? ExpectedEffectHash,
    GovernedLoopEffectReconciliationJournalStage Stage,
    DateTimeOffset CreatedAtUtc,
    string ContentHash);
