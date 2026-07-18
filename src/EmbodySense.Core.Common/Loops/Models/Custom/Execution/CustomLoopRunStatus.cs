namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public enum CustomLoopRunStatus
{
    Unknown = 0,
    Admitted = 1,
    Running = 2,
    PauseRequested = 3,
    Paused = 4,
    CancelRequested = 5,
    Completed = 6,
    Failed = 7,
    Cancelled = 8,
    NeedsReview = 9
}
