using EmbodySense.Core.Application.Loops.Retry.Models;

namespace EmbodySense.Core.Application.Loops.Retry;

/// <summary>Attempts durable opt-in retry scheduling for one exact retained node failure.</summary>
public interface IGovernedLoopRetryNodeExecutor
{
    /// <summary>Evaluates current posture and durably parks an admitted next attempt without dispatching it early.</summary>
    Task<GovernedLoopRetryExecutionResult> ScheduleAsync(
        GovernedLoopRetryExecutionRequest request,
        CancellationToken cancellationToken = default);
}
