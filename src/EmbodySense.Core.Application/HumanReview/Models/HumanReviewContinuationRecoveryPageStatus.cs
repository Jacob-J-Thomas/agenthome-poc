namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed bounded discovery result for Human Review continuation recovery.</summary>
public enum HumanReviewContinuationRecoveryPageStatus
{
    /// <summary>No supported page result was supplied.</summary>
    Unknown = 0,

    /// <summary>The canonical source was scanned successfully.</summary>
    Current = 1,

    /// <summary>The supplied cursor, trusted observation, or canonical source content was invalid.</summary>
    Invalid = 2,

    /// <summary>The canonical source could not complete a bounded scan.</summary>
    Unavailable = 3,
}
