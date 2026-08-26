using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Aggregates the canonical trigger, retained schedule-retry, and Wake boundaries before schedule authoring exists.</summary>
/// <remarks>
/// This composition parks a discovered schedule without dispatching it. Phase 2 has no production
/// <c>IScheduleGovernedPayloadSource</c> until the later authorized Web-authoring slice supplies one, so the exact
/// persisted state is returned as <see cref="ScheduleEvaluationStatus.NeedsReview"/> instead of fabricating a payload or
/// terminating unrelated Trigger and Wake delivery. Retained schedule admission finalization remains safe to retry
/// because it consumes only already-persisted envelopes and exact run-store evidence. All dependencies borrow the
/// factory-owned workspace stores and no request, connection, or surface supplies a dispatch dependency. See
/// https://github.com/Jacob-J-Thomas/agenthome-poc/issues/565.
/// </remarks>
internal sealed class GovernedLoopWaitAndTriggerOneShotServices : IGovernedLoopLocalOneShotServices
{
    private const string ScheduleUnavailableReason = "schedule-payload-source-unavailable";
    private readonly IScheduleStorePort _schedules;
    private readonly GovernedLoopSleepService _sleep;
    private readonly GovernedLoopScheduleAdmissionRetryService _scheduleRetries;
    private readonly ITriggerQueueQueryPort _triggerQueue;
    private readonly TriggerWorkerService _triggers;

    internal GovernedLoopWaitAndTriggerOneShotServices(
        ICustomLoopRunStore runs,
        IScheduleStorePort schedules,
        ITriggerQueueQueryPort triggerQueue,
        TriggerWorkerService triggers,
        GovernedLoopSleepService sleep,
        int scheduleRetryPageSize)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
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
    public async Task<ScheduleEvaluationResult?> EvaluateScheduleOnceAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduleId);
        var read = await _schedules.ReadAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        return read.Status switch
        {
            ScheduleStoreReadStatus.Found when read.State is not null => new ScheduleEvaluationResult(
                ScheduleEvaluationStatus.NeedsReview,
                ScheduleUnavailableReason,
                read.State),
            ScheduleStoreReadStatus.NotFound => new ScheduleEvaluationResult(
                ScheduleEvaluationStatus.NotFound,
                "schedule-not-found",
                null),
            ScheduleStoreReadStatus.Backpressured => new ScheduleEvaluationResult(
                ScheduleEvaluationStatus.Backpressured,
                "schedule-store-backpressured",
                null),
            ScheduleStoreReadStatus.Unavailable => new ScheduleEvaluationResult(
                ScheduleEvaluationStatus.Unavailable,
                "schedule-store-unavailable",
                null),
            _ => new ScheduleEvaluationResult(
                ScheduleEvaluationStatus.Corrupt,
                "schedule-store-corrupt",
                null),
        };
    }

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
