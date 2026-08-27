namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies whether an exact continuation release yielded conclusive durable evidence.</summary>
public enum HumanReviewContinuationReleaseStatus
{
    /// <summary>No supported release result was supplied.</summary>
    Unknown = 0,

    /// <summary>The port produced one exact conclusive completion.</summary>
    Completed = 1,

    /// <summary>The port could not establish a release result and no terminal mutation is safe.</summary>
    Unavailable = 2,

    /// <summary>The port cannot prove whether an irreversible boundary was crossed; the coordinator must retain state without redispatch.</summary>
    Ambiguous = 3,

    /// <summary>The port conclusively rejected malformed prepared release input without crossing its release boundary.</summary>
    Invalid = 4,
}
