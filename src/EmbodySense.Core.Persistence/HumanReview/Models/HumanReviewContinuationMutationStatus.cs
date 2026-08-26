namespace EmbodySense.Core.Persistence.HumanReview.Models;

/// <summary>Identifies the closed result of one canonical Human Review continuation state mutation.</summary>
public enum HumanReviewContinuationMutationStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact state transition was atomically committed.</summary>
    Committed = 1,

    /// <summary>The exact requested artifact was already present after canonical reconciliation.</summary>
    Replayed = 2,

    /// <summary>The canonical run or continuation state changed incompatibly.</summary>
    Conflict = 3,

    /// <summary>The named canonical run was not found.</summary>
    NotFound = 4,

    /// <summary>The input or canonical Human Review state was invalid or corrupt.</summary>
    Invalid = 5,

    /// <summary>The bounded canonical persistence operation could not be completed or reconciled.</summary>
    Unavailable = 6,

    /// <summary>The canonical run artifact quota rejected the transition without publishing it.</summary>
    LimitExceeded = 7,
}
