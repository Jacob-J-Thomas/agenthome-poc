using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Aggregates the canonical schedule evaluation, trigger, retained schedule-retry, and Wake boundaries.</summary>
/// <remarks>
/// Schedule evaluation receives only the factory-composed runtime facade, whose current evidence rereads the canonical
/// schedule definition, immutable graph revision, authority, capability, and payload source. Retained schedule admission
/// finalization remains safe to retry because it consumes only already-persisted envelopes and exact run-store evidence.
/// All dependencies borrow factory-owned workspace stores and no request, connection, or surface supplies a dispatch
/// dependency.
/// </remarks>
internal sealed class GovernedLoopWaitAndTriggerOneShotServices : IGovernedLoopLocalOneShotServices
{
    private readonly ScheduleRuntimeFacade _scheduleRuntime;
    private readonly GovernedLoopSleepService _sleep;
    private readonly GovernedLoopScheduleAdmissionRetryService _scheduleRetries;
    private readonly ITriggerQueueQueryPort _triggerQueue;
    private readonly TriggerWorkerService _triggers;

    internal GovernedLoopWaitAndTriggerOneShotServices(
        ICustomLoopRunStore runs,
        ScheduleRuntimeFacade scheduleRuntime,
        ITriggerQueueQueryPort triggerQueue,
        TriggerWorkerService triggers,
        GovernedLoopSleepService sleep,
        int scheduleRetryPageSize)
    {
        _scheduleRuntime = scheduleRuntime ?? throw new ArgumentNullException(nameof(scheduleRuntime));
        _triggerQueue = triggerQueue ?? throw new ArgumentNullException(nameof(triggerQueue));
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
        _scheduleRetries = new GovernedLoopScheduleAdmissionRetryService(
            runs ?? throw new ArgumentNullException(nameof(runs)),
            triggerQueue,
            triggers,
            scheduleRetryPageSize);
    }

    /// <inheritdoc />
    public Task<ScheduleEvaluationResult?> EvaluateScheduleOnceAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduleId);
        return EvaluateAsync(scheduleId, cancellationToken);
    }

    private async Task<ScheduleEvaluationResult?> EvaluateAsync(ScheduleId scheduleId, CancellationToken cancellationToken)
        => await _scheduleRuntime.EvaluateOnceAsync(scheduleId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TriggerQueueSnapshot?> ReadTriggerQueueAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => await _triggerQueue.GetSnapshotAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TriggerWorkerRunResult?> RunTriggerOnceAsync(
        TriggerWorkerRunRequest request,
        CancellationToken cancellationToken = default)
        => await _triggers.RunOnceAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<GovernedLoopLocalWorkResult?> RetryScheduleAdmissionOnceAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => await _scheduleRetries.RetryOnceAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<GovernedLoopWakeResult?> WakeOnceAsync(
        GovernedLoopWakeRequest request,
        CancellationToken cancellationToken = default)
        => await _sleep.WakeAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<GovernedLoopWakeResult?> ReconcileWakeOnceAsync(
        GovernedLoopWakeReconciliationRequest request,
        CancellationToken cancellationToken = default)
        => await _sleep.ReconcileAsync(request, cancellationToken).ConfigureAwait(false);
}
