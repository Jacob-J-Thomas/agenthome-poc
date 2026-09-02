using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Retains immutable probe intent and the exact case/effect/source identities reserved before callback.</summary>
/// <param name="OperationId">The independent probe operation identity.</param>
/// <param name="RequestHash">The canonical reserved intent hash.</param>
/// <param name="Case">The exact case reference.</param>
/// <param name="EffectHead">The exact retained reconciliation-required effect head.</param>
/// <param name="Source">The exact retained source registration.</param>
/// <param name="Contract">The exact probe contract pin.</param>
/// <param name="ReservedAtUtc">The trusted reservation time.</param>
public sealed record GovernedLoopEffectReconciliationProbeReservation(
    string OperationId,
    string RequestHash,
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectAttempt EffectHead,
    GovernedLoopEffectReconciliationEvidenceSource Source,
    GovernedLoopEffectReconciliationContractMetadata Contract,
    DateTimeOffset ReservedAtUtc)
{
    /// <summary>Gets the independent operation identity.</summary>
    public string OperationId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(OperationId, nameof(OperationId));
    /// <summary>Gets the canonical intent hash.</summary>
    public string RequestHash { get; } = GovernedLoopEffectReconciliationModelGuard.RequireSha256(RequestHash, nameof(RequestHash));
    /// <summary>Gets the exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));
    /// <summary>Gets the exact retained effect head.</summary>
    public GovernedLoopEffectAttempt EffectHead { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredProbeEffect(EffectHead, Case, nameof(EffectHead));
    /// <summary>Gets the exact retained source registration.</summary>
    public GovernedLoopEffectReconciliationEvidenceSource Source { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredProbeSource(Source, Case, Contract, nameof(Source));
    /// <summary>Gets the exact probe contract pin.</summary>
    public GovernedLoopEffectReconciliationContractMetadata Contract { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredMetadata(Contract, nameof(Contract));
    /// <summary>Gets the trusted UTC reservation time.</summary>
    public DateTimeOffset ReservedAtUtc { get; } = GovernedLoopEffectReconciliationModelGuard.RequireUtc(ReservedAtUtc, nameof(ReservedAtUtc));
}
