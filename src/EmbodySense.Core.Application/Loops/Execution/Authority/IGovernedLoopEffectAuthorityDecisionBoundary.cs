using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

/// <summary>Exposes the exact appended direct authority decision to a protected continuation before it may cross an effect boundary.</summary>
public interface IGovernedLoopEffectAuthorityDecisionBoundary : IGovernedLoopEffectAuthorityBoundary
{
    /// <summary>Gets the exact workspace authority transaction that fences authority revalidation and effect dispatch.</summary>
    ICapabilityAuthorityTransaction AuthorityTransaction { get; }

    /// <summary>Evaluates and appends authority, then supplies that exact immutable decision to the at-most-once continuation.</summary>
    Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteWithDecisionAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken = default);
}
