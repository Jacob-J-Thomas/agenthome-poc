namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed canonical-store result of recording one completion or retirement intent.</summary>
public enum HumanReviewContinuationStoreMutationStatus
{
    /// <summary>No supported mutation result was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact terminal intent was committed.</summary>
    Committed = 1,

    /// <summary>The exact terminal intent was already committed and safely replayed.</summary>
    Replayed = 2,

    /// <summary>The canonical candidate changed, was claimed by another worker, or has a conflicting terminal result.</summary>
    Conflict = 3,

    /// <summary>The named canonical run, wake, claim, or reservation no longer exists.</summary>
    NotFound = 4,

    /// <summary>The supplied intent or canonical state is invalid or corrupt.</summary>
    Invalid = 5,

    /// <summary>Canonical persistence could not complete the bounded mutation.</summary>
    Unavailable = 6,

    /// <summary>Canonical retention limits rejected the mutation without changing state.</summary>
    LimitExceeded = 7,
}
