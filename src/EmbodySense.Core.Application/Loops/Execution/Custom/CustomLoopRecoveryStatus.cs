namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public enum CustomLoopRecoveryStatus
{
    Unchanged = 1,
    Paused = 2,
    Cancelled = 3,
    NeedsReview = 4,
    Conflict = 5,
    Failed = 6
}
