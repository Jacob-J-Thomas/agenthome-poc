namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Identifies the closed durable disposition of one exact wake delivery.</summary>
public enum GovernedLoopWakeDisposition
{
    /// <summary>Continuation intent was durably prepared before the external continuation call.</summary>
    Prepared = 1,

    /// <summary>The exact continuation was conclusively committed.</summary>
    Committed = 2,

    /// <summary>The deterministic wake was already observed and cannot mint another continuation.</summary>
    Duplicate = 3,

    /// <summary>The delivery arrived after its exact eligible boundary.</summary>
    Late = 4,

    /// <summary>The checkpoint no longer names the current frontier generation or visit.</summary>
    Stale = 5,

    /// <summary>An optimistic owner or evidence conflict prevented continuation.</summary>
    Conflict = 6,

    /// <summary>The exact run was cancelled before continuation.</summary>
    Cancelled = 7,

    /// <summary>The exact run or wake boundary expired before continuation.</summary>
    Expired = 8,

    /// <summary>The exact run was paused before continuation.</summary>
    Paused = 9,

    /// <summary>The exact run required human review before continuation.</summary>
    ReviewBlocked = 10,

    /// <summary>An open provider or effect attempt made continuation ambiguous.</summary>
    AmbiguousAttempt = 11,

    /// <summary>The exact wake failed conclusively without fabricating completion.</summary>
    Failed = 12
}
