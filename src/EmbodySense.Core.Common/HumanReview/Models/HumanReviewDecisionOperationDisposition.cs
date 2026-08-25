namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the durable disposition of one decision operation.</summary>
public enum HumanReviewDecisionOperationDisposition
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>A terminal approve, reject, or cancel decision was accepted.</summary>
    Accepted = 1,
    /// <summary>A request-for-information decision was accepted while the frontier remains parked.</summary>
    InformationRequested = 2,
    /// <summary>The operation was denied without accepting a decision.</summary>
    Denied = 3,
    /// <summary>The operation conflicted with durable request or operation state.</summary>
    Conflict = 4,
    /// <summary>The operation arrived after the review request expired.</summary>
    Expired = 5
}
