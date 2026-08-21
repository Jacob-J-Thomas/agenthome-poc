using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.EffectAttempts;

/// <summary>Adds atomic side-effect-free preparation claims without changing the frozen effect-attempt store port.</summary>
public interface IGovernedLoopEffectAttemptPreparationClaimStore
{
    /// <summary>
    /// Durably creates or exactly replays a prepared intent. For a new intent, the side-effect-free preparation
    /// claim is evaluated while the store excludes cleanup.
    /// </summary>
    Task<GovernedLoopEffectAttemptStoreResult> BeginWithPreparationClaimAsync(
        GovernedLoopEffectAttempt prepared,
        Func<CancellationToken, Task<bool>> preparationClaim,
        CancellationToken cancellationToken = default);
}
