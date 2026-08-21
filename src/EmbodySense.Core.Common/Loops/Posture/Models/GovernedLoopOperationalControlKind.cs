namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Identifies the closed controls admitted by the local-background operational plane.</summary>
public enum GovernedLoopOperationalControlKind
{
    /// <summary>Represents a missing or unrecognized public control token.</summary>
    Unknown = 0,

    /// <summary>Requests checkpoint-bound run pause.</summary>
    PauseRun = 1,

    /// <summary>Requests durable run cancellation.</summary>
    CancelRun = 2,

    /// <summary>Explicitly resumes one paused run.</summary>
    ResumeRun = 3,

    /// <summary>Optimistically disables one schedule.</summary>
    DisableSchedule = 4,

    /// <summary>Optimistically enables one schedule.</summary>
    EnableSchedule = 5,

    /// <summary>Optimistically cancels one exact queued delivery.</summary>
    CancelDelivery = 6,

    /// <summary>Cancels a bounded captured set of nonterminal deliveries for one loop.</summary>
    CancelPendingDeliveries = 7
}
