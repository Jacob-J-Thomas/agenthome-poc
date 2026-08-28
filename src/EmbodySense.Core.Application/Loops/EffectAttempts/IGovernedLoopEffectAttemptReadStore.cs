using EmbodySense.Core.Application.Loops.EffectAttempts.Models;

namespace EmbodySense.Core.Application.Loops.EffectAttempts;

/// <summary>Reads one current canonical effect-attempt head without acquiring execution ownership or changing durable state.</summary>
public interface IGovernedLoopEffectAttemptReadStore
{
    /// <summary>Reads the exact current immutable head for one stable operation generation.</summary>
    /// <remarks>Implementations must not resume an attempt, acquire a lease, repair head evidence, dispatch work, or mutate any durable artifact while serving this read.</remarks>
    /// <param name="operationId">The exact stable idempotency operation identity.</param>
    /// <param name="effectGeneration">The exact positive operation generation.</param>
    /// <param name="cancellationToken">Cancels the bounded read before a closed result is available.</param>
    /// <returns>A detached current head or a closed missing, corrupt, or unavailable posture.</returns>
    Task<GovernedLoopEffectAttemptReadResult> ReadAsync(string operationId, long effectGeneration, CancellationToken cancellationToken = default);
}
