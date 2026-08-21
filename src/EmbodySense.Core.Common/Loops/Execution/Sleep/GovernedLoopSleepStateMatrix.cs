using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

/// <summary>Defines the closed schema-1 wake and local-coordinator state transitions.</summary>
public static class GovernedLoopSleepStateMatrix
{
    /// <summary>Gets whether one exact wake disposition may advance to another.</summary>
    public static bool IsWakeTransitionAllowed(GovernedLoopWakeDisposition current, GovernedLoopWakeDisposition next)
        => current switch
        {
            GovernedLoopWakeDisposition.Prepared => next is GovernedLoopWakeDisposition.Committed
                or GovernedLoopWakeDisposition.AmbiguousAttempt
                or GovernedLoopWakeDisposition.Failed,
            GovernedLoopWakeDisposition.AmbiguousAttempt => next is GovernedLoopWakeDisposition.Committed
                or GovernedLoopWakeDisposition.Failed,
            _ => false
        };

    /// <summary>Gets whether one exact coordinator lifecycle posture may advance to another.</summary>
    public static bool IsCoordinatorTransitionAllowed(GovernedLoopCoordinatorStatus current, GovernedLoopCoordinatorStatus next)
        => current switch
        {
            GovernedLoopCoordinatorStatus.Starting => next is GovernedLoopCoordinatorStatus.Running
                or GovernedLoopCoordinatorStatus.Stopping
                or GovernedLoopCoordinatorStatus.Stopped
                or GovernedLoopCoordinatorStatus.Failed,
            GovernedLoopCoordinatorStatus.Running => next is GovernedLoopCoordinatorStatus.Stopping
                or GovernedLoopCoordinatorStatus.Failed,
            GovernedLoopCoordinatorStatus.Stopping => next is GovernedLoopCoordinatorStatus.Stopped
                or GovernedLoopCoordinatorStatus.Failed,
            _ => false
        };
}
