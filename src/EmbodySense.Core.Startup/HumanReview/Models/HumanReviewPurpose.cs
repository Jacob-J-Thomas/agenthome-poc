namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the exact parked work governed by a projected Human Review request.</summary>
public enum HumanReviewPurpose
{
    /// <summary>No supported purpose was supplied.</summary>
    Unknown = 0,
    /// <summary>The request governs release of one admitted continuation.</summary>
    Continuation = 1,
    /// <summary>The request governs one conclusively not-yet-dispatched effect attempt.</summary>
    PreDispatchEffect = 2
}
