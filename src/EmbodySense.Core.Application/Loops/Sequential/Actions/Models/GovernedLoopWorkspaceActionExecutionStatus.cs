namespace EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

/// <summary>Identifies one closed workspace Action orchestration posture.</summary>
public enum GovernedLoopWorkspaceActionExecutionStatus
{
    /// <summary>No supported outcome was selected.</summary>
    Unknown = 0,

    /// <summary>One exact outcome was committed or replayed without redispatch.</summary>
    Completed = 1,

    /// <summary>The effect was conclusively stopped before mutation.</summary>
    Rejected = 2,

    /// <summary>The effect or its durable evidence is ambiguous and requires review.</summary>
    NeedsReview = 3,
}
