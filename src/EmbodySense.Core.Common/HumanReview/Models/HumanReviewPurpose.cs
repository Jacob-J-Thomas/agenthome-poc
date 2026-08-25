namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the exact parked work that a Human Review request may govern.</summary>
public enum HumanReviewPurpose
{
    /// <summary>No supported purpose was supplied.</summary>
    Unknown = 0,
    /// <summary>The request governs release of one already admitted continuation.</summary>
    Continuation = 1,
    /// <summary>The request governs one conclusively not-yet-dispatched effect attempt.</summary>
    PreDispatchEffect = 2
}
