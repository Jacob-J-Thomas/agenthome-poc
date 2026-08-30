namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the exact non-approval action durably completed for an accepted Human Review decision.</summary>
public enum HumanReviewDecisionActionDisposition
{
    /// <summary>No supported action disposition was retained.</summary>
    Unknown = 0,

    /// <summary>The exact authored failure action was applied for a rejected decision.</summary>
    Rejected = 1,

    /// <summary>The canonical cancellation action was applied for a cancelled decision.</summary>
    Cancelled = 2,

    /// <summary>The exact review-blocked frontier remained parked after information was requested.</summary>
    InformationParked = 3,
}
