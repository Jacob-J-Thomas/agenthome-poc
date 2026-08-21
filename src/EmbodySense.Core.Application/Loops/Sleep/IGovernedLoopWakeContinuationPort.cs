using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Continues and reconciles one exact waiting frontier through a stable idempotency operation.</summary>
public interface IGovernedLoopWakeContinuationPort
{
    /// <summary>Attempts the exact idempotent continuation after durable prepared wake evidence exists.</summary>
    Task<GovernedLoopWakeContinuationResult?> ContinueAsync(GovernedLoopWakeContinuationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reconciles whether the exact continuation operation committed without redispatching it.</summary>
    Task<GovernedLoopWakeContinuationResult?> ReconcileAsync(GovernedLoopWakeContinuationRequest request, CancellationToken cancellationToken = default);
}
