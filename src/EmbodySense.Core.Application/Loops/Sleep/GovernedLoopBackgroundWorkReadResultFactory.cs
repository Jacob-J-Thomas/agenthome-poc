using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Creates detached background-work read results at the application port boundary.</summary>
public static class GovernedLoopBackgroundWorkReadResultFactory
{
    /// <summary>Creates one result whose candidate collections and values cannot alias adapter-owned inputs.</summary>
    /// <param name="status">The closed enumeration outcome.</param>
    /// <param name="scheduleCandidates">Exact schedule identities eligible for bounded evaluation, or <see langword="null"/> for a malformed adapter result.</param>
    /// <param name="wakeCandidates">Exact checkpoint wake requests eligible for bounded delivery, or <see langword="null"/> for a malformed adapter result.</param>
    /// <param name="wakeReconciliationCandidates">Exact ambiguous or prepared wakes eligible for bounded reconciliation, or <see langword="null"/> for a malformed adapter result.</param>
    /// <param name="schedulePageTruncated">Whether more schedule candidates remain outside this page.</param>
    /// <param name="wakePageTruncated">Whether more ordinary-wake candidates remain outside this page.</param>
    /// <param name="wakeReconciliationPageTruncated">Whether more reconciliation candidates remain outside this page.</param>
    /// <returns>A detached result that preserves malformed nulls for fail-closed contract validation.</returns>
    public static GovernedLoopBackgroundWorkReadResult CreateDetached(
        GovernedLoopBackgroundWorkReadStatus status,
        IReadOnlyList<ScheduleId>? scheduleCandidates,
        IReadOnlyList<GovernedLoopWakeRequest>? wakeCandidates,
        IReadOnlyList<GovernedLoopWakeReconciliationRequest>? wakeReconciliationCandidates,
        bool schedulePageTruncated = false,
        bool wakePageTruncated = false,
        bool wakeReconciliationPageTruncated = false)
    {
        var scheduleStatus = FamilyStatus(status, scheduleCandidates);
        var wakeStatus = FamilyStatus(status, wakeCandidates);
        var wakeReconciliationStatus = FamilyStatus(status, wakeReconciliationCandidates);
        return new(
            Summarize(scheduleStatus, wakeStatus, wakeReconciliationStatus),
            scheduleStatus,
            wakeStatus,
            wakeReconciliationStatus,
            schedulePageTruncated,
            wakePageTruncated,
            wakeReconciliationPageTruncated,
            CopySchedules(scheduleCandidates)!,
            CopyWakes(wakeCandidates)!,
            CopyWakeReconciliations(wakeReconciliationCandidates)!);
    }

    /// <summary>Creates one result with independently classified, detached candidate families.</summary>
    public static GovernedLoopBackgroundWorkReadResult CreateDetached(
        GovernedLoopBackgroundWorkReadStatus scheduleStatus,
        IReadOnlyList<ScheduleId>? scheduleCandidates,
        GovernedLoopBackgroundWorkReadStatus wakeStatus,
        IReadOnlyList<GovernedLoopWakeRequest>? wakeCandidates,
        GovernedLoopBackgroundWorkReadStatus wakeReconciliationStatus,
        IReadOnlyList<GovernedLoopWakeReconciliationRequest>? wakeReconciliationCandidates,
        bool schedulePageTruncated = false,
        bool wakePageTruncated = false,
        bool wakeReconciliationPageTruncated = false)
        => new(
            Summarize(scheduleStatus, wakeStatus, wakeReconciliationStatus),
            scheduleStatus,
            wakeStatus,
            wakeReconciliationStatus,
            schedulePageTruncated,
            wakePageTruncated,
            wakeReconciliationPageTruncated,
            CopySchedules(scheduleCandidates)!,
            CopyWakes(wakeCandidates)!,
            CopyWakeReconciliations(wakeReconciliationCandidates)!);

    private static GovernedLoopBackgroundWorkReadStatus Summarize(
        GovernedLoopBackgroundWorkReadStatus scheduleStatus,
        GovernedLoopBackgroundWorkReadStatus wakeStatus,
        GovernedLoopBackgroundWorkReadStatus wakeReconciliationStatus)
    {
        GovernedLoopBackgroundWorkReadStatus[] statuses = [scheduleStatus, wakeStatus, wakeReconciliationStatus];
        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Found))
        {
            return GovernedLoopBackgroundWorkReadStatus.Found;
        }

        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Corrupt))
        {
            return GovernedLoopBackgroundWorkReadStatus.Corrupt;
        }

        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Unavailable))
        {
            return GovernedLoopBackgroundWorkReadStatus.Unavailable;
        }

        return statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Backpressured)
            ? GovernedLoopBackgroundWorkReadStatus.Backpressured
            : GovernedLoopBackgroundWorkReadStatus.Empty;
    }

    private static GovernedLoopBackgroundWorkReadStatus FamilyStatus<T>(
        GovernedLoopBackgroundWorkReadStatus status,
        IReadOnlyList<T>? candidates)
        => status == GovernedLoopBackgroundWorkReadStatus.Found && candidates is { Count: 0 }
            ? GovernedLoopBackgroundWorkReadStatus.Empty
            : status;

    private static IReadOnlyList<ScheduleId>? CopySchedules(IReadOnlyList<ScheduleId>? candidates)
        => candidates is null
            ? null
            : Array.AsReadOnly(candidates.Select(item => item is null ? null! : CopyScheduleId(item)).ToArray());

    private static IReadOnlyList<GovernedLoopWakeRequest>? CopyWakes(IReadOnlyList<GovernedLoopWakeRequest>? candidates)
        => candidates is null
            ? null
            : Array.AsReadOnly(candidates.Select(item => item is null ? null! : item with { }).ToArray());

    private static IReadOnlyList<GovernedLoopWakeReconciliationRequest>? CopyWakeReconciliations(IReadOnlyList<GovernedLoopWakeReconciliationRequest>? candidates)
        => candidates is null
            ? null
            : Array.AsReadOnly(candidates.Select(item => item is null ? null! : item with { }).ToArray());

    private static ScheduleId CopyScheduleId(ScheduleId scheduleId)
    {
        _ = ScheduleId.TryParse(scheduleId.Value, out var copy);
        return copy!;
    }
}
