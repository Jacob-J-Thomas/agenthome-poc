namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed result of a canonical claim, completion, or retirement mutation.</summary>
public enum HumanReviewContinuationStoreMutationStatus
{
    /// <summary>No supported mutation result was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact mutation was committed.</summary>
    Committed = 1,

    /// <summary>The exact mutation was already committed and safely replayed.</summary>
    Replayed = 2,

    /// <summary>The candidate, claim, or terminal state changed concurrently.</summary>
    Conflict = 3,

    /// <summary>The named canonical state no longer exists.</summary>
    NotFound = 4,

    /// <summary>The supplied input or canonical state is invalid or corrupt.</summary>
    Invalid = 5,

    /// <summary>The bounded canonical operation could not complete.</summary>
    Unavailable = 6,

    /// <summary>Canonical retention limits rejected the mutation without changing state.</summary>
    LimitExceeded = 7,
}
