using EmbodySense.Core.Application.Loops.Wait.Models;

namespace EmbodySense.Core.Application.Loops.Wait;

/// <summary>Recovers a bounded page of retained Wait parks and continuations that lack their next durable boundary.</summary>
public interface IGovernedLoopWaitRecoveryPort
{
    /// <summary>Reconciles at most <paramref name="maximumCount"/> retained candidates without redispatching completed work.</summary>
    /// <param name="maximumCount">The positive bounded candidate limit.</param>
    /// <param name="cancellationToken">The token used before each candidate crosses its next durable boundary.</param>
    /// <returns>The bounded recovery outcome.</returns>
    Task<GovernedLoopWaitRecoveryResult> RecoverAsync(int maximumCount, CancellationToken cancellationToken = default);
}
