using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.EffectAttempts;

/// <summary>Persists immutable value-free effect-attempt intent and direct protocol successors.</summary>
public interface IGovernedLoopEffectAttemptStore
{
    /// <summary>Reads and, when unfinished, exclusively resumes one stable operation generation without catalog dependencies.</summary>
    Task<GovernedLoopEffectAttemptStoreResult> ResumeAsync(
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>Durably creates or exactly replays a prepared intent before an irreversible dispatch boundary.</summary>
    Task<GovernedLoopEffectAttemptStoreResult> BeginAsync(
        GovernedLoopEffectAttempt prepared,
        CancellationToken cancellationToken = default);

    /// <summary>Commits one direct hash-linked successor while the caller retains exact generation ownership.</summary>
    Task<GovernedLoopEffectAttemptStoreResult> CompareExchangeAsync(
        string expectedContentHash,
        GovernedLoopEffectAttempt replacement,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken = default);
}
