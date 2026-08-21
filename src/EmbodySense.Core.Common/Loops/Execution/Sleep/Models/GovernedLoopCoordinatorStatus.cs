namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Identifies one closed local background-coordinator lifecycle posture.</summary>
public enum GovernedLoopCoordinatorStatus
{
    /// <summary>The exact owner is starting and has not yet admitted background work.</summary>
    Starting = 1,

    /// <summary>The exact owner is running and may admit bounded background work.</summary>
    Running = 2,

    /// <summary>The exact owner is stopping and admits no new background work.</summary>
    Stopping = 3,

    /// <summary>The exact owner stopped without fabricating work completion.</summary>
    Stopped = 4,

    /// <summary>The exact owner terminated because of a retained coordinator failure.</summary>
    Failed = 5
}
