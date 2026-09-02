using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns exact immutable graph and run input reconstructed for reconciliation.</summary>
/// <param name="Status">The closed input-read disposition.</param>
/// <param name="Case">The exact echoed case reference when safely established.</param>
/// <param name="Binding">The exact echoed reconciliation binding when safely established.</param>
/// <param name="EffectHead">The exact current reconciliation-required effect-attempt head when found.</param>
/// <param name="Frontier">The exact current ReviewBlocked frontier containing the matching activation when found.</param>
/// <param name="Input">The detached bounded canonical input only when found.</param>
public sealed record GovernedLoopEffectReconciliationInputReadResult(
    GovernedLoopEffectReconciliationInputReadStatus Status,
    GovernedLoopEffectReconciliationCaseReference? Case,
    GovernedLoopEffectReconciliationBinding? Binding,
    GovernedLoopEffectAttempt? EffectHead,
    GovernedLoopFrontierPosture? Frontier,
    GovernedActuatorInputEvidence? Input)
{
    /// <summary>Gets the validated closed input-read disposition.</summary>
    public GovernedLoopEffectReconciliationInputReadStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference? Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyInputReadCase(Status, Case, nameof(Case));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding? Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyInputReadBinding(Status, Case, Binding, EffectHead, Frontier, Input, nameof(Binding));

    /// <summary>Gets the detached exact current reconciliation-required effect-attempt head.</summary>
    public GovernedLoopEffectAttempt? EffectHead { get; } = GovernedLoopEffectReconciliationModelGuard.CopyInputReadEffect(Status, EffectHead, nameof(EffectHead));

    /// <summary>Gets the detached exact current ReviewBlocked frontier.</summary>
    public GovernedLoopFrontierPosture? Frontier { get; } = GovernedLoopEffectReconciliationModelGuard.CopyInputReadFrontier(Status, Frontier, nameof(Frontier));

    /// <summary>Gets detached bounded canonical input.</summary>
    public GovernedActuatorInputEvidence? Input { get; } = GovernedLoopEffectReconciliationModelGuard.CopyInputReadInput(Status, Input, nameof(Input));
}
