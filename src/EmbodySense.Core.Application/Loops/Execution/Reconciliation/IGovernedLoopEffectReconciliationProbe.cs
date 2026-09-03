using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Reads bounded external evidence for one exact reconciliation case without exercising actuator behavior.</summary>
public interface IGovernedLoopEffectReconciliationProbe
{
    /// <summary>Produces one case-bound observation without dispatching, retrying, recovering, compensating, or otherwise invoking the original actuator.</summary>
    /// <param name="request">The value-free registered target selected by the trusted reservation.</param>
    /// <param name="cancellationToken">A token that cancels the read-only probe invocation.</param>
    /// <returns>The exact case-bound observation or a fail-closed disposition.</returns>
    Task<GovernedLoopEffectReconciliationProbeInvocationResult> ProbeAsync(GovernedLoopEffectReconciliationProbeInvocationRequest request, CancellationToken cancellationToken = default);
}
