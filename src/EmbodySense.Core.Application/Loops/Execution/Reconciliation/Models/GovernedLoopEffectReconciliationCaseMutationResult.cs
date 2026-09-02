using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns the exact result of the atomic reconciliation case and optional effect-head compare-exchange.</summary>
/// <param name="Status">The closed atomic compare-exchange disposition.</param>
/// <param name="Case">The detached exact current case when safely observed.</param>
/// <param name="EffectHead">The detached exact current effect-attempt head when safely observed.</param>
public sealed record GovernedLoopEffectReconciliationCaseMutationResult(
    GovernedLoopEffectReconciliationCaseMutationStatus Status,
    GovernedLoopEffectReconciliationCase? Case,
    GovernedLoopEffectAttempt? EffectHead)
{
    /// <summary>Gets the validated closed atomic compare-exchange disposition.</summary>
    public GovernedLoopEffectReconciliationCaseMutationStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets a detached immutable current case snapshot.</summary>
    public GovernedLoopEffectReconciliationCase? Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyMutationResultCase(Status, Case, EffectHead, nameof(Case));

    /// <summary>Gets a detached immutable current effect-attempt head.</summary>
    public GovernedLoopEffectAttempt? EffectHead { get; } = GovernedLoopEffectReconciliationModelGuard.CopyMutationResultEffect(Status, Case, EffectHead, nameof(EffectHead));
}
