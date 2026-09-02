using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests one immutable reconciliation resolution bound to an exact case version.</summary>
/// <param name="Case">The exact immutable case reference.</param>
/// <param name="Binding">The exact reconciliation binding that the resolution must retain.</param>
public sealed record GovernedLoopEffectReconciliationResolutionReadRequest(
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectReconciliationBinding Binding)
{
    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyBoundBinding(Case, Binding, nameof(Binding));
}
