using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Adapts only the canonical sleep service into the shared one-shot worker.</summary>
internal sealed class GovernedLoopWaitOnlyOneShotServices : IGovernedLoopLocalOneShotServices
{
    private readonly GovernedLoopSleepService _sleep;

    internal GovernedLoopWaitOnlyOneShotServices(GovernedLoopSleepService sleep)
        => _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));

    public async Task<GovernedLoopWakeResult?> WakeOnceAsync(
        GovernedLoopWakeRequest request,
        CancellationToken cancellationToken = default)
        => await _sleep.WakeAsync(request, cancellationToken).ConfigureAwait(false);

    public async Task<GovernedLoopWakeResult?> ReconcileWakeOnceAsync(
        GovernedLoopWakeReconciliationRequest request,
        CancellationToken cancellationToken = default)
        => await _sleep.ReconcileAsync(request, cancellationToken).ConfigureAwait(false);

    public Task<ScheduleEvaluationResult?> EvaluateScheduleOnceAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The AgentRuntime Wait plane does not own schedule evaluation.");

    public Task<TriggerQueueSnapshot?> ReadTriggerQueueAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The AgentRuntime Wait plane does not own trigger queues.");

    public Task<TriggerWorkerRunResult?> RunTriggerOnceAsync(
        TriggerWorkerRunRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The AgentRuntime Wait plane does not own trigger dispatch.");

    public Task<GovernedLoopLocalWorkResult?> RetryScheduleAdmissionOnceAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The AgentRuntime Wait plane does not own schedule admission retries.");
}
