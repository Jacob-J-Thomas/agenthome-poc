namespace EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

/// <summary>Classifies one bounded command Action execution projection.</summary>
public enum GovernedLoopCommandActionExecutionStatus
{
    /// <summary>The request or current posture stopped before process launch.</summary>
    Rejected = 0,

    /// <summary>A successful conclusive command result is durable.</summary>
    Completed = 1,

    /// <summary>A conclusive failed command result is durable.</summary>
    Failed = 2,

    /// <summary>The effect may have crossed its external boundary and requires reconciliation.</summary>
    NeedsReview = 3,
}
