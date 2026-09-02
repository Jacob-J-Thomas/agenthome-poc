using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

internal sealed record GovernedLoopEffectReconciliationOperationReceipt(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    string Purpose,
    string CaseId,
    long CaseVersion,
    string CaseContentHash,
    string BindingHash,
    GovernedLoopEffectReconciliationCaseMutationStatus Status,
    string? EffectContentHash,
    DateTimeOffset CommittedAtUtc,
    string ContentHash);
