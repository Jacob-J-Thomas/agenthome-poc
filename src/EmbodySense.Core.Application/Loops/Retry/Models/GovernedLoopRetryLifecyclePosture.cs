namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Identifies the current authenticated lifecycle posture evaluated before automatic retry.</summary>
public enum GovernedLoopRetryLifecyclePosture
{
    /// <summary>No trustworthy lifecycle posture is available.</summary>
    Unknown = 0,
    /// <summary>The run remains active and eligible for policy evaluation.</summary>
    Active,
    /// <summary>An authenticated user paused the run.</summary>
    Paused,
    /// <summary>An authenticated user cancelled the run.</summary>
    Cancelled,
    /// <summary>The run is already blocked on human review.</summary>
    ReviewBlocked,
    /// <summary>The run is otherwise inactive or terminal.</summary>
    Inactive,
}
