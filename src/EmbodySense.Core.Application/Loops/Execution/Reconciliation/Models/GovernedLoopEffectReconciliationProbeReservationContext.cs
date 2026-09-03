using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Retains trusted probe intent for persistence without exposing it to a probe callback.</summary>
/// <param name="Case">The exact case reference reserved for the probe.</param>
/// <param name="Binding">The exact reconciliation binding.</param>
/// <param name="Contract">The exact registered probe contract.</param>
/// <param name="EffectHead">The exact retained reconciliation-required effect head.</param>
/// <param name="Source">The exact retained source registration.</param>
/// <param name="Target">The exact value-free registered target.</param>
/// <param name="InputFingerprint">The canonical input fingerprint; raw input is never retained.</param>
public sealed record GovernedLoopEffectReconciliationProbeReservationContext(
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectReconciliationBinding Binding,
    GovernedLoopEffectReconciliationContractMetadata Contract,
    GovernedLoopEffectAttempt EffectHead,
    GovernedLoopEffectReconciliationEvidenceSource Source,
    GovernedLoopEffectReconciliationProbeTarget Target,
    string InputFingerprint)
{
    /// <summary>Gets the detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));
    /// <summary>Gets the detached exact binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyBoundBinding(Case, Binding, nameof(Binding));
    /// <summary>Gets the detached exact contract.</summary>
    public GovernedLoopEffectReconciliationContractMetadata Contract { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredMetadata(Contract, nameof(Contract));
    /// <summary>Gets the detached exact reconciliation-required effect head.</summary>
    public GovernedLoopEffectAttempt EffectHead { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeEffect(EffectHead, Binding, nameof(EffectHead));
    /// <summary>Gets the detached exact source registration.</summary>
    public GovernedLoopEffectReconciliationEvidenceSource Source { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeSource(Source, Case, Binding, Contract, nameof(Source));
    /// <summary>Gets the detached exact value-free probe target.</summary>
    public GovernedLoopEffectReconciliationProbeTarget Target { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredProbeTarget(Target, EffectHead, nameof(Target));
    /// <summary>Gets the canonical input fingerprint.</summary>
    public string InputFingerprint { get; } = GovernedLoopEffectReconciliationModelGuard.RequireSha256(InputFingerprint, nameof(InputFingerprint));
}
