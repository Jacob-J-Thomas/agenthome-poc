using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Provides current server-owned authorization for exact reconciliation purposes, cases, and bindings.</summary>
public interface IGovernedLoopEffectReconciliationAuthorizationSource
{
    /// <summary>Evaluates current authority without changing the case, invoking a probe, or dispatching an actuator.</summary>
    /// <param name="request">The exact purpose, immutable case reference, and reconciliation binding.</param>
    /// <param name="cancellationToken">A token that cancels authorization evaluation.</param>
    /// <returns>The exact echoed authorization result or a fail-closed disposition.</returns>
    Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default);
}
