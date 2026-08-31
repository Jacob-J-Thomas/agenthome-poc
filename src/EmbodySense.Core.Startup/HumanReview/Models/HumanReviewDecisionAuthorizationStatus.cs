namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the server-owned outcome for one exact Human Review authorization evaluation.</summary>
public enum HumanReviewDecisionAuthorizationStatus
{
    /// <summary>No authorization outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The server-owned reviewer is currently authorized for the exact request.</summary>
    Ready = 1,
    /// <summary>The server-owned reviewer is authenticated but not eligible for the exact request.</summary>
    Denied = 2,
    /// <summary>The authority source could not produce an unambiguous answer.</summary>
    Unavailable = 3
}
