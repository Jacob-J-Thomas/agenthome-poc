using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

/// <summary>Immutable terminal probe result envelope retained before the response is acknowledged.</summary>
internal sealed record GovernedLoopEffectReconciliationProbeObservationArtifact(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    string CaseId,
    long CaseVersion,
    string CaseContentHash,
    string BindingHash,
    string EffectContentHash,
    GovernedLoopEffectReconciliationProbeInvocationStatus Status,
    string? ObservationJson,
    long ResultCaseVersion,
    string? ResultCaseContentHash,
    DateTimeOffset CommittedAtUtc,
    string ContentHash);
