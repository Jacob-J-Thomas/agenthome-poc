namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Identifies one executor-neutral governed-run lifecycle state.</summary>
public enum GovernedLoopRunStatus
{
    /// <summary>No supported state was supplied.</summary>
    Unknown = 0,
    /// <summary>The immutable execution inputs have been admitted.</summary>
    Admitted,
    /// <summary>The execution is eligible to make forward progress.</summary>
    Running,
    /// <summary>The execution is durably waiting for an admitted wake condition.</summary>
    Waiting,
    /// <summary>A pause has been requested but has not reached a durable safe point.</summary>
    PauseRequested,
    /// <summary>The execution is durably paused at a safe point.</summary>
    Paused,
    /// <summary>Cancellation has been requested but has not reached a durable safe point.</summary>
    CancelRequested,
    /// <summary>The execution completed successfully.</summary>
    Completed,
    /// <summary>The execution terminated with a conclusive failure.</summary>
    Failed,
    /// <summary>The execution terminated after cancellation.</summary>
    Cancelled,
    /// <summary>The execution requires an explicit review or reconciliation decision.</summary>
    NeedsReview
}
