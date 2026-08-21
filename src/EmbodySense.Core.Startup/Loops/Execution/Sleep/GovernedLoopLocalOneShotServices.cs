using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

internal sealed class GovernedLoopLocalOneShotServices : IGovernedLoopLocalOneShotServices
{
    private readonly GovernedLoopSleepService _sleep;
    private readonly GovernedLoopScheduleAdmissionRetryService _scheduleRetries;
    private readonly ScheduleRuntimeFacade _schedules;
    private readonly ITriggerQueueQueryPort _triggerQueue;
    private readonly TriggerWorkerService _triggers;

    internal GovernedLoopLocalOneShotServices(
        ScheduleRuntimeFacade schedules,
        ICustomLoopRunStore runs,
        ITriggerQueueQueryPort triggerQueue,
        TriggerWorkerService triggers,
        GovernedLoopSleepService sleep,
        int scheduleRetryPageSize)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _triggerQueue = triggerQueue ?? throw new ArgumentNullException(nameof(triggerQueue));
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
        _scheduleRetries = new GovernedLoopScheduleAdmissionRetryService(runs, triggerQueue, triggers, scheduleRetryPageSize);
    }

    public async Task<ScheduleEvaluationResult?> EvaluateScheduleOnceAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
        => await _schedules.EvaluateOnceAsync(scheduleId, cancellationToken).ConfigureAwait(false);

    public async Task<TriggerQueueSnapshot?> ReadTriggerQueueAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => await _triggerQueue.GetSnapshotAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);

    public async Task<TriggerWorkerRunResult?> RunTriggerOnceAsync(
        TriggerWorkerRunRequest request,
        CancellationToken cancellationToken = default)
        => await _triggers.RunOnceAsync(request, cancellationToken).ConfigureAwait(false);

    public async Task<GovernedLoopLocalWorkResult?> RetryScheduleAdmissionOnceAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => await _scheduleRetries.RetryOnceAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);

    public async Task<GovernedLoopWakeResult?> WakeOnceAsync(
        GovernedLoopWakeRequest request,
        CancellationToken cancellationToken = default)
        => await _sleep.WakeAsync(request, cancellationToken).ConfigureAwait(false);

    public async Task<GovernedLoopWakeResult?> ReconcileWakeOnceAsync(
        GovernedLoopWakeReconciliationRequest request,
        CancellationToken cancellationToken = default)
        => await _sleep.ReconcileAsync(request, cancellationToken).ConfigureAwait(false);
}
