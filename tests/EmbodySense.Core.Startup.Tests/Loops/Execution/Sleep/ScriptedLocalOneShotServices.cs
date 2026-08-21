using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class ScriptedLocalOneShotServices : IGovernedLoopLocalOneShotServices
{
    internal Func<ScheduleId, CancellationToken, Task<ScheduleEvaluationResult?>> EvaluateSchedule { get; set; }
        = static (_, _) => Task.FromResult<ScheduleEvaluationResult?>(
            new ScheduleEvaluationResult(ScheduleEvaluationStatus.NotDue, "not-due", null));

    internal Func<DateTimeOffset, CancellationToken, Task<TriggerQueueSnapshot?>> ReadTriggerQueue { get; set; }
        = static (_, _) => Task.FromResult<TriggerQueueSnapshot?>(null);

    internal Func<TriggerWorkerRunRequest, CancellationToken, Task<TriggerWorkerRunResult?>> RunTrigger { get; set; }
        = static (_, _) => Task.FromResult<TriggerWorkerRunResult?>(
            new TriggerWorkerRunResult(TriggerWorkerSelectionStatus.Empty, null, null));

    internal Func<DateTimeOffset, CancellationToken, Task<GovernedLoopLocalWorkResult?>> RetryScheduleAdmission { get; set; }
        = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
            new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "schedule-retry-empty"));

    internal Func<GovernedLoopWakeRequest, CancellationToken, Task<GovernedLoopWakeResult?>> Wake { get; set; }
        = static (_, _) => Task.FromResult<GovernedLoopWakeResult?>(
            new GovernedLoopWakeResult(GovernedLoopWakeResultStatus.NotEligible));

    internal Func<GovernedLoopWakeReconciliationRequest, CancellationToken, Task<GovernedLoopWakeResult?>> ReconcileWake { get; set; }
        = static (_, _) => Task.FromResult<GovernedLoopWakeResult?>(
            new GovernedLoopWakeResult(GovernedLoopWakeResultStatus.NotEligible));

    public Task<ScheduleEvaluationResult?> EvaluateScheduleOnceAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
        => EvaluateSchedule(scheduleId, cancellationToken);

    public Task<TriggerQueueSnapshot?> ReadTriggerQueueAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => ReadTriggerQueue(observedAtUtc, cancellationToken);

    public Task<TriggerWorkerRunResult?> RunTriggerOnceAsync(
        TriggerWorkerRunRequest request,
        CancellationToken cancellationToken = default)
        => RunTrigger(request, cancellationToken);

    public Task<GovernedLoopLocalWorkResult?> RetryScheduleAdmissionOnceAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => RetryScheduleAdmission(observedAtUtc, cancellationToken);

    public Task<GovernedLoopWakeResult?> WakeOnceAsync(
        GovernedLoopWakeRequest request,
        CancellationToken cancellationToken = default)
        => Wake(request, cancellationToken);

    public Task<GovernedLoopWakeResult?> ReconcileWakeOnceAsync(
        GovernedLoopWakeReconciliationRequest request,
        CancellationToken cancellationToken = default)
        => ReconcileWake(request, cancellationToken);
}
