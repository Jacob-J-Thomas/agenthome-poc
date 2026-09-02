using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one exact immutable reconciliation case read.</summary>
/// <param name="Status">The closed exact-read disposition.</param>
/// <param name="Case">The detached exact case only when canonical content was safely observed.</param>
public sealed record GovernedLoopEffectReconciliationCaseReadResult(
    GovernedLoopEffectReconciliationCaseReadStatus Status,
    GovernedLoopEffectReconciliationCase? Case)
{
    /// <summary>Gets the validated closed exact-read disposition.</summary>
    public GovernedLoopEffectReconciliationCaseReadStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets a detached immutable case snapshot.</summary>
    public GovernedLoopEffectReconciliationCase? Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyCaseReadPayload(Status, Case, nameof(Case));
}
