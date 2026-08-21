using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Persistence.Triggers.Schedules;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

/// <summary>Projects stable bounded schedule and sleeping-checkpoint catalog pages through the shared background-work port.</summary>
/// <remarks>Each family advances independently by the last emitted key and wraps to canonical order after the tail.</remarks>
public sealed class GovernedLoopBackgroundWorkSource : IGovernedLoopBackgroundWorkSource
{
    private readonly SemaphoreSlim _pageGate = new(1, 1);
    private readonly ScheduleStore _scheduleStore;
    private readonly GovernedLoopSleepStore _sleepStore;
    private ScheduleId? _scheduleCursor;
    private string? _wakeCursor;
    private string? _wakeReconciliationCursor;

    /// <summary>Creates a durable background-work source over exact schedule and sleep stores.</summary>
    public GovernedLoopBackgroundWorkSource(ScheduleStore scheduleStore, GovernedLoopSleepStore sleepStore)
    {
        _scheduleStore = scheduleStore ?? throw new ArgumentNullException(nameof(scheduleStore));
        _sleepStore = sleepStore ?? throw new ArgumentNullException(nameof(sleepStore));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopBackgroundWorkReadResult?> ReadAsync(
        GovernedLoopBackgroundWorkFamily family,
        DateTimeOffset observedAtUtc,
        int pageMax,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(family)
            || !GovernedLoopBackgroundWorkContract.IsValidReadRequest(observedAtUtc, pageMax))
        {
            return Empty(GovernedLoopBackgroundWorkReadStatus.Corrupt);
        }

        await _pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return family switch
            {
                GovernedLoopBackgroundWorkFamily.Schedule => await ReadSchedulePageAsync(observedAtUtc, pageMax, cancellationToken).ConfigureAwait(false),
                GovernedLoopBackgroundWorkFamily.Wake => await ReadWakePageAsync(family, observedAtUtc, pageMax, _wakeCursor, cancellationToken).ConfigureAwait(false),
                GovernedLoopBackgroundWorkFamily.WakeReconciliation => await ReadWakePageAsync(family, observedAtUtc, pageMax, _wakeReconciliationCursor, cancellationToken).ConfigureAwait(false),
                _ => Empty(GovernedLoopBackgroundWorkReadStatus.Corrupt)
            };
        }
        finally
        {
            _pageGate.Release();
        }
    }

    private async Task<GovernedLoopBackgroundWorkReadResult> ReadSchedulePageAsync(
        DateTimeOffset observedAtUtc,
        int pageMax,
        CancellationToken cancellationToken)
    {
        var schedules = await _scheduleStore.ReadCandidatesAsync(
            observedAtUtc,
            pageMax,
            _scheduleCursor,
            cancellationToken).ConfigureAwait(false);
        if (schedules.Status == ScheduleStoreReadStatus.Found && schedules.Candidates.Count > 0)
        {
            _scheduleCursor = schedules.Candidates[^1];
        }

        return GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            Map(schedules.Status),
            schedules.Candidates,
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            schedulePageTruncated: schedules.PageTruncated);
    }

    private async Task<GovernedLoopBackgroundWorkReadResult> ReadWakePageAsync(
        GovernedLoopBackgroundWorkFamily family,
        DateTimeOffset observedAtUtc,
        int pageMax,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await _sleepStore.ReadCandidatesAsync(
            family,
            observedAtUtc,
            pageMax,
            cursor,
            cancellationToken).ConfigureAwait(false);
        if (family == GovernedLoopBackgroundWorkFamily.Wake && result.WakeCandidates.Count > 0)
        {
            _wakeCursor = result.WakeCandidates[^1].CheckpointId;
        }
        else if (family == GovernedLoopBackgroundWorkFamily.WakeReconciliation
            && result.WakeReconciliationCandidates.Count > 0)
        {
            _wakeReconciliationCursor = result.WakeReconciliationCandidates[^1].CheckpointId;
        }

        return result;
    }

    private static GovernedLoopBackgroundWorkReadStatus Map(ScheduleStoreReadStatus status)
        => status switch
        {
            ScheduleStoreReadStatus.Found => GovernedLoopBackgroundWorkReadStatus.Found,
            ScheduleStoreReadStatus.NotFound => GovernedLoopBackgroundWorkReadStatus.Empty,
            ScheduleStoreReadStatus.Backpressured => GovernedLoopBackgroundWorkReadStatus.Backpressured,
            ScheduleStoreReadStatus.Corrupt => GovernedLoopBackgroundWorkReadStatus.Corrupt,
            _ => GovernedLoopBackgroundWorkReadStatus.Unavailable
        };

    private static GovernedLoopBackgroundWorkReadResult Empty(GovernedLoopBackgroundWorkReadStatus status)
        => GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(status, [], [], []);
}
