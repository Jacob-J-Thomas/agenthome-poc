using EmbodySense.Core.Application.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

/// <summary>Revalidates exact governed-loop authority and controls one irreversible effect continuation.</summary>
public interface IGovernedLoopEffectAuthorityBoundary
{
    /// <summary>Evaluates and records authority, invoking the continuation at most once and only for a durable direct decision.</summary>
    /// <typeparam name="TResult">The protected continuation result type.</typeparam>
    /// <param name="request">The exact admitted run, node, attempt, and effect requirement.</param>
    /// <param name="commit">The smallest continuation that crosses the named irreversible boundary.</param>
    /// <param name="cancellationToken">A token that cancels before or during evaluation and commit.</param>
    /// <returns>The durable decision and protected continuation outcome.</returns>
    Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken = default);
}
