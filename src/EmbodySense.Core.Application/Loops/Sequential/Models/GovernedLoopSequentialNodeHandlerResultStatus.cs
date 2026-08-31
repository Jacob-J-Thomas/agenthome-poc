namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Classifies one exact node handler's bounded result.</summary>
public enum GovernedLoopSequentialNodeHandlerResultStatus
{
    /// <summary>No supported result was produced.</summary>
    Unknown = 0,
    /// <summary>The node completed and retained exact outcome evidence.</summary>
    Completed,
    /// <summary>The node was definitively rejected and retained exact evidence.</summary>
    Rejected,
    /// <summary>The node stopped for durable review with exact ambiguity evidence.</summary>
    NeedsReview,
    /// <summary>The node durably parked for an intentional human-review decision before any ambiguous outcome exists.</summary>
    ReviewPending,
}
