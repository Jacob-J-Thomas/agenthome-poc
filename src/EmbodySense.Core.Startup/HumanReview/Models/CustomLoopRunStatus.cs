namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the detached status of the enclosing custom-loop run.</summary>
public enum CustomLoopRunStatus
{
    /// <summary>No supported run status was supplied.</summary>
    Unknown = 0,
    /// <summary>The run was admitted.</summary>
    Admitted = 1,
    /// <summary>The run is executing.</summary>
    Running = 2,
    /// <summary>A pause was requested.</summary>
    PauseRequested = 3,
    /// <summary>The run is paused.</summary>
    Paused = 4,
    /// <summary>A cancellation was requested.</summary>
    CancelRequested = 5,
    /// <summary>The run completed successfully.</summary>
    Completed = 6,
    /// <summary>The run failed.</summary>
    Failed = 7,
    /// <summary>The run was cancelled.</summary>
    Cancelled = 8,
    /// <summary>The run is blocked on Human Review.</summary>
    NeedsReview = 9,
    /// <summary>The run is durably waiting for one admitted wake condition.</summary>
    Waiting = 10
}
