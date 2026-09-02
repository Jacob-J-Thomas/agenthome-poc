using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one exact immutable reconciliation resolution read.</summary>
/// <param name="Status">The closed resolution-read disposition.</param>
/// <param name="Resolution">The detached immutable resolution only when found.</param>
public sealed record GovernedLoopEffectReconciliationResolutionReadResult(
    GovernedLoopEffectReconciliationResolutionReadStatus Status,
    GovernedLoopEffectReconciliationResolution? Resolution)
{
    /// <summary>Gets the validated closed resolution-read disposition.</summary>
    public GovernedLoopEffectReconciliationResolutionReadStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets a detached immutable resolution.</summary>
    public GovernedLoopEffectReconciliationResolution? Resolution { get; } = GovernedLoopEffectReconciliationModelGuard.CopyResolutionPayload(Status, Resolution, nameof(Resolution));
}
