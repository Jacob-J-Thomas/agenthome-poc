using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Owns the shared lifecycle eligibility used by control execution and operational posture.</summary>
public static class CustomLoopLifecycleControlEligibility
{
    /// <summary>Determines whether the current lifecycle admits the requested run control.</summary>
    public static bool IsEligible(GovernedLoopOperationalControlKind kind, CustomLoopRunStatus status)
        => kind switch
        {
            GovernedLoopOperationalControlKind.PauseRun => status is CustomLoopRunStatus.Running or CustomLoopRunStatus.Waiting,
            GovernedLoopOperationalControlKind.CancelRun => status is CustomLoopRunStatus.Admitted
                or CustomLoopRunStatus.Running
                or CustomLoopRunStatus.Waiting
                or CustomLoopRunStatus.PauseRequested
                or CustomLoopRunStatus.Paused
                or CustomLoopRunStatus.CancelRequested,
            GovernedLoopOperationalControlKind.ResumeRun => status == CustomLoopRunStatus.Paused,
            _ => false
        };

    /// <summary>Returns deterministic currently eligible controls for posture projection.</summary>
    public static IReadOnlyList<GovernedLoopOperationalControlKind> GetEligible(CustomLoopRunStatus status)
        => Array.AsReadOnly(Enum.GetValues<GovernedLoopOperationalControlKind>()
            .Where(kind => IsEligible(kind, status))
            .ToArray());
}
