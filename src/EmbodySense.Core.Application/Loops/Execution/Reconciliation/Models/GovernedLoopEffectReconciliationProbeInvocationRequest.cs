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
public sealed record GovernedLoopEffectReconciliationProbeInvocationRequest(
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectReconciliationBinding Binding,
    GovernedLoopEffectReconciliationContractMetadata Contract,
    GovernedActuatorInputEvidence Input)
{
    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyBoundBinding(Case, Binding, nameof(Binding));

    /// <summary>Gets a detached exact actuator and probe contract.</summary>
    public GovernedLoopEffectReconciliationContractMetadata Contract { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredMetadata(Contract, nameof(Contract));

    /// <summary>Gets a detached exact reconstructed input.</summary>
    public GovernedActuatorInputEvidence Input { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredInput(Input, nameof(Input));
}
