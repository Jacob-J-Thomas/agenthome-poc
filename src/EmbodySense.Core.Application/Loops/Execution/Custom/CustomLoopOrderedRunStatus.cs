using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public enum CustomLoopOrderedRunStatus
{
    Unknown = 0,
    Completed = 1,
    Failed = 2,
    NeedsReview = 3,
    Conflict = 4,
    InvalidState = 5,
    NotFound = 6,
    Cancelled = 7,
    Paused = 8
}
