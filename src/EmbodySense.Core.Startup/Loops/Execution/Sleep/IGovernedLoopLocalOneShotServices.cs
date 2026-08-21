using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Aggregates canonical one-shot subsystem boundaries without owning a background lifetime or retry policy.</summary>
public interface IGovernedLoopLocalOneShotServices
{
    /// <summary>Evaluates and durably settles at most one occurrence for one exact schedule.</summary>
    Task<ScheduleEvaluationResult?> EvaluateScheduleOnceAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one bounded validated trigger queue snapshot.</summary>
    Task<TriggerQueueSnapshot?> ReadTriggerQueueAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Selects and durably dispatches at most one exact trigger entry.</summary>
    Task<TriggerWorkerRunResult?> RunTriggerOnceAsync(
        TriggerWorkerRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reselects at most one retained deferred or serialized schedule admission under its exact original identity.</summary>
    Task<GovernedLoopLocalWorkResult?> RetryScheduleAdmissionOnceAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Admits at most one exact checkpoint wake continuation.</summary>
    Task<GovernedLoopWakeResult?> WakeOnceAsync(
        GovernedLoopWakeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reconciles at most one exact prepared or ambiguous wake continuation.</summary>
    Task<GovernedLoopWakeResult?> ReconcileWakeOnceAsync(
        GovernedLoopWakeReconciliationRequest request,
        CancellationToken cancellationToken = default);
}
