namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the durable lifecycle posture of one immutable Human Review request.</summary>
public enum HumanReviewLifecycleStatus
{
    /// <summary>No supported lifecycle status was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact frontier remains parked awaiting a terminal decision or information request.</summary>
    Pending = 1,
    /// <summary>Information was requested and the exact frontier remains parked.</summary>
    AwaitingInformation = 2,
    /// <summary>One exact approval decision was accepted; later orchestration must still revalidate before release.</summary>
    Approved = 3,
    /// <summary>One exact rejection decision was accepted.</summary>
    Rejected = 4,
    /// <summary>One exact cancellation decision was accepted.</summary>
    Cancelled = 5,
    /// <summary>The request passed its deadline without an accepted terminal decision.</summary>
    Expired = 6,
    /// <summary>A durable conflict or replacement made the request permanently unreleasable.</summary>
    Superseded = 7,
    /// <summary>Corrupt or competing evidence made the request permanently unreleasable.</summary>
    Conflicted = 8
}
