namespace EmbodySense.Core.Application.Loops.Sleep.Models;

internal enum GovernedLoopSleepPostureDecision
{
    Eligible,
    Stale,
    Cancelled,
    Expired,
    Paused,
    ReviewBlocked,
    AmbiguousAttempt,
    Invalid
}
