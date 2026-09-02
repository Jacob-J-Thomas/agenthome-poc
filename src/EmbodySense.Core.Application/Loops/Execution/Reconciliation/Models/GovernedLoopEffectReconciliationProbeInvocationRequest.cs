using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Supplies one registered read-only probe with exact immutable reconciliation input.</summary>
/// <param name="Case">The exact immutable case reference.</param>
/// <param name="Binding">The exact reconciliation binding.</param>
/// <param name="Contract">The exact registered actuator and probe pin.</param>
/// <param name="Input">The exact reconstructed bounded graph and run input.</param>
/// <param name="EffectHead">The exact retained reconciliation-required effect head.</param>
/// <param name="Source">The exact retained source registration used for this invocation.</param>
public sealed record GovernedLoopEffectReconciliationProbeInvocationRequest(
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectReconciliationBinding Binding,
    GovernedLoopEffectReconciliationContractMetadata Contract,
    GovernedActuatorInputEvidence Input,
    GovernedLoopEffectAttempt EffectHead,
    GovernedLoopEffectReconciliationEvidenceSource Source)
{
    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyBoundBinding(Case, Binding, nameof(Binding));

    /// <summary>Gets a detached exact actuator and probe contract.</summary>
    public GovernedLoopEffectReconciliationContractMetadata Contract { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredMetadata(Contract, nameof(Contract));

    /// <summary>Gets a detached exact reconstructed input.</summary>
    public GovernedActuatorInputEvidence Input { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredInput(Input, nameof(Input));

    /// <summary>Gets a detached exact retained reconciliation-required effect head.</summary>
    public GovernedLoopEffectAttempt EffectHead { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeEffect(EffectHead, Binding, nameof(EffectHead));

    /// <summary>Gets a detached exact retained source registration.</summary>
    public GovernedLoopEffectReconciliationEvidenceSource Source { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbeSource(Source, Case, Binding, Contract, nameof(Source));
}
