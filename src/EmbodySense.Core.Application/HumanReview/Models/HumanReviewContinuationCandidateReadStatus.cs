namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed outcome of rereading one continuation candidate from canonical state.</summary>
public enum HumanReviewContinuationCandidateReadStatus
{
    /// <summary>No supported read result was supplied.</summary>
    Unknown = 0,

    /// <summary>A detached current candidate exactly matched the query.</summary>
    Current = 1,

    /// <summary>The named run, review, reservation, wake, or claim no longer exists.</summary>
    Missing = 2,

    /// <summary>Canonical retained state was malformed, forward-versioned, or internally inconsistent.</summary>
    Corrupt = 3,

    /// <summary>Canonical state changed or no longer exactly matches the query.</summary>
    Stale = 4,

    /// <summary>The canonical source could not complete a bounded read.</summary>
    Unavailable = 5,
}
