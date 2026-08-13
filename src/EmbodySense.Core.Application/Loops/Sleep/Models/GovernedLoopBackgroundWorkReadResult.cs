using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Returns one bounded detached snapshot of durable local background-work candidates.</summary>
public sealed record GovernedLoopBackgroundWorkReadResult
{
    internal GovernedLoopBackgroundWorkReadResult(
        GovernedLoopBackgroundWorkReadStatus status,
        GovernedLoopBackgroundWorkReadStatus scheduleStatus,
        GovernedLoopBackgroundWorkReadStatus wakeStatus,
        GovernedLoopBackgroundWorkReadStatus wakeReconciliationStatus,
        bool schedulePageTruncated,
        bool wakePageTruncated,
        bool wakeReconciliationPageTruncated,
        IReadOnlyList<ScheduleId> scheduleCandidates,
        IReadOnlyList<GovernedLoopWakeRequest> wakeCandidates,
        IReadOnlyList<GovernedLoopWakeReconciliationRequest> wakeReconciliationCandidates)
    {
        ScheduleStatus = scheduleStatus;
        WakeStatus = wakeStatus;
        WakeReconciliationStatus = wakeReconciliationStatus;
        SchedulePageTruncated = schedulePageTruncated;
        WakePageTruncated = wakePageTruncated;
        WakeReconciliationPageTruncated = wakeReconciliationPageTruncated;
        Status = status;
        ScheduleCandidates = scheduleCandidates;
        WakeCandidates = wakeCandidates;
        WakeReconciliationCandidates = wakeReconciliationCandidates;
    }

    /// <summary>Gets a compatibility summary of the independently classified candidate families.</summary>
    /// <remarks>A healthy found family takes precedence so this summary never suppresses its candidates. Consumers should inspect the requested family status.</remarks>
    public GovernedLoopBackgroundWorkReadStatus Status { get; }

    /// <summary>Gets the closed schedule-candidate enumeration outcome.</summary>
    public GovernedLoopBackgroundWorkReadStatus ScheduleStatus { get; }

    /// <summary>Gets the closed ordinary-wake enumeration outcome.</summary>
    public GovernedLoopBackgroundWorkReadStatus WakeStatus { get; }

    /// <summary>Gets the closed wake-reconciliation enumeration outcome.</summary>
    public GovernedLoopBackgroundWorkReadStatus WakeReconciliationStatus { get; }

    /// <summary>Gets whether additional schedule candidates remain outside this stable bounded page.</summary>
    public bool SchedulePageTruncated { get; }

    /// <summary>Gets whether additional ordinary-wake candidates remain outside this stable bounded page.</summary>
    public bool WakePageTruncated { get; }

    /// <summary>Gets whether additional reconciliation candidates remain outside this stable bounded page.</summary>
    public bool WakeReconciliationPageTruncated { get; }

    /// <summary>Gets exact detached schedule identities eligible for bounded evaluation.</summary>
    public IReadOnlyList<ScheduleId> ScheduleCandidates { get; }

    /// <summary>Gets exact detached checkpoint wake requests eligible for bounded delivery.</summary>
    public IReadOnlyList<GovernedLoopWakeRequest> WakeCandidates { get; }

    /// <summary>Gets exact detached ambiguous or prepared wakes eligible for bounded reconciliation.</summary>
    public IReadOnlyList<GovernedLoopWakeReconciliationRequest> WakeReconciliationCandidates { get; }

}
