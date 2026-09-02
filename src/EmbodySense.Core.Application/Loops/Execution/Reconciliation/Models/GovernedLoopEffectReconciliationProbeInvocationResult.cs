using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one bounded read-only reconciliation probe observation.</summary>
/// <param name="Status">The closed probe-invocation disposition.</param>
/// <param name="Observation">The detached immutable observation only when safely produced.</param>
public sealed record GovernedLoopEffectReconciliationProbeInvocationResult(
    GovernedLoopEffectReconciliationProbeInvocationStatus Status,
    GovernedLoopEffectReconciliationObservation? Observation)
{
    /// <summary>Gets the validated closed probe-invocation disposition.</summary>
    public GovernedLoopEffectReconciliationProbeInvocationStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets a detached immutable observation.</summary>
    public GovernedLoopEffectReconciliationObservation? Observation { get; } = GovernedLoopEffectReconciliationModelGuard.CopyProbePayload(Status, Observation, nameof(Observation));
}
