using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests reconstruction of exact immutable graph and run input for one reconciliation case.</summary>
/// <param name="Case">The exact immutable case reference.</param>
/// <param name="Binding">The exact graph, run, effect, and actuator binding.</param>
public sealed record GovernedLoopEffectReconciliationInputReadRequest(
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectReconciliationBinding Binding)
{
    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyBoundBinding(Case, Binding, nameof(Binding));
}
