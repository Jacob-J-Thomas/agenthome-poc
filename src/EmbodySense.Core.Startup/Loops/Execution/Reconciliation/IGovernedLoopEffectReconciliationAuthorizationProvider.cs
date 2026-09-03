using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

/// <summary>Supplies current authenticated actor and scope authorization for exact reconciliation purposes.</summary>
/// <remarks>Implementations derive identity from a trusted interface session. They must never trust caller payload identity, case content, or Human Review approval as reconciliation authority.</remarks>
public interface IGovernedLoopEffectReconciliationAuthorizationProvider
{
    /// <summary>Reads current authority for one exact server-composed request.</summary>
    /// <param name="request">The redacted exact request and hidden-binding hash.</param>
    /// <param name="cancellationToken">A token that cancels the current authority read.</param>
    /// <returns>A request-bound ready, denied, or unavailable result.</returns>
    Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default);
}
