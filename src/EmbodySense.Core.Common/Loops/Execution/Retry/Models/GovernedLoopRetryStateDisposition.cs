namespace EmbodySense.Core.Common.Loops.Execution.Retry.Models;

/// <summary>Identifies the durable posture of one exact retry series.</summary>
public enum GovernedLoopRetryStateDisposition
{
    /// <summary>The retry posture is undefined.</summary>
    Unknown = 0,
    /// <summary>The classified failure is retained and a retry decision is being prepared.</summary>
    FailureRetained,
    /// <summary>The exact next attempt is sleeping until its durable wake instant.</summary>
    Scheduled,
    /// <summary>The wake is due and the next-attempt reservation is not yet durable.</summary>
    Due,
    /// <summary>The exact next-attempt budget is reserved before dispatch.</summary>
    Reserved,
    /// <summary>The reserved attempt reached its canonical dispatch boundary.</summary>
    Dispatched,
    /// <summary>The reserved attempt produced one conclusive terminal outcome.</summary>
    AttemptCompleted,
    /// <summary>No further retry is admitted because a finite bound was exhausted.</summary>
    Exhausted,
    /// <summary>Current lifecycle, authority, or dependency posture stopped automatic retry.</summary>
    Stopped,
    /// <summary>Evidence is ambiguous, conflicting, incomplete, or corrupt and requires review.</summary>
    NeedsReview,
}
