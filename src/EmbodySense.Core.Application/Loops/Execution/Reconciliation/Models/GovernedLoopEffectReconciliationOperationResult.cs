using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one detached reconciliation orchestration result without exposing authority or private actuator payloads.</summary>
/// <param name="Status">The closed operation disposition.</param>
/// <param name="Case">The exact current case when the operation safely observed one.</param>
/// <param name="EffectHead">The exact current effect head when the case result safely includes it.</param>
public sealed record GovernedLoopEffectReconciliationOperationResult(
    GovernedLoopEffectReconciliationOperationStatus Status,
    GovernedLoopEffectReconciliationCase? Case,
    GovernedLoopEffectAttempt? EffectHead)
{
    /// <summary>Gets the closed operation disposition.</summary>
    public GovernedLoopEffectReconciliationOperationStatus Status { get; } = GovernedLoopEffectReconciliationOperationResultGuard.RequireStatus(Status);

    /// <summary>Gets a detached immutable case, when safely available.</summary>
    public GovernedLoopEffectReconciliationCase? Case { get; } = GovernedLoopEffectReconciliationOperationResultGuard.CopyCase(Status, Case);

    /// <summary>Gets a detached immutable effect head, when safely available.</summary>
    public GovernedLoopEffectAttempt? EffectHead { get; } = GovernedLoopEffectReconciliationOperationResultGuard.CopyEffect(Status, EffectHead);
}
