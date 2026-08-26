namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the closed durable outcome of one continuation wake.</summary>
public enum HumanReviewContinuationOutcome
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact claimed continuation completed before its lease expired.</summary>
    Completed = 1,
    /// <summary>The wake was retired because the review or continuation was cancelled.</summary>
    Cancelled = 2,
    /// <summary>The wake was retired only after its exact wake-expiry boundary elapsed.</summary>
    Expired = 3,
    /// <summary>The wake was retired because its exact bound state was superseded.</summary>
    Superseded = 4,
    /// <summary>The wake was retired because corruption, conflict, or ambiguous effect certainty blocks release.</summary>
    Blocked = 5
}
