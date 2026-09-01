namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the detached lifecycle posture of one immutable Human Review request.</summary>
public enum HumanReviewLifecycleStatus
{
    /// <summary>No supported lifecycle status was supplied.</summary>
    Unknown = 0,
    /// <summary>The frontier remains parked awaiting a terminal decision or information.</summary>
    Pending = 1,
    /// <summary>Information was requested and the frontier remains parked.</summary>
    AwaitingInformation = 2,
    /// <summary>An approval was accepted and release must revalidate exact evidence.</summary>
    Approved = 3,
    /// <summary>A rejection was accepted.</summary>
    Rejected = 4,
    /// <summary>A cancellation was accepted.</summary>
    Cancelled = 5,
    /// <summary>The request passed its deadline without an accepted terminal decision.</summary>
    Expired = 6,
    /// <summary>Durable state drift made the request permanently unreleasable.</summary>
    Superseded = 7,
    /// <summary>Corrupt or competing evidence made the request permanently unreleasable.</summary>
    Conflicted = 8
}
