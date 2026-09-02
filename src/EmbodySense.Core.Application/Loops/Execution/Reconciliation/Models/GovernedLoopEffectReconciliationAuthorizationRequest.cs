using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests current server-owned authorization for one exact reconciliation purpose, case, and binding.</summary>
/// <param name="Purpose">The bounded canonical reconciliation purpose.</param>
/// <param name="Case">The exact immutable case reference.</param>
/// <param name="Binding">The exact run, effect, actuator, and evidence-source binding.</param>
public sealed record GovernedLoopEffectReconciliationAuthorizationRequest(
    string Purpose,
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectReconciliationBinding Binding)
{
    /// <summary>Gets the validated bounded canonical reconciliation purpose.</summary>
    public string Purpose { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(Purpose, nameof(Purpose));

    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyBoundBinding(Case, Binding, nameof(Binding));
}
