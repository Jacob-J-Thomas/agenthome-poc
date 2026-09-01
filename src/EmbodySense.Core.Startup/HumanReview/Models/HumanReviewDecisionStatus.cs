namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the closed outcome of one Human Review decision operation.</summary>
public enum HumanReviewDecisionStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>A decision was durably accepted.</summary>
    Accepted = 1,
    /// <summary>A request for information was durably accepted.</summary>
    InformationRequested = 2,
    /// <summary>The exact operation replayed its prior durable result.</summary>
    Replayed = 3,
    /// <summary>The server-owned reviewer was denied.</summary>
    Denied = 4,
    /// <summary>The operation conflicted with canonical state.</summary>
    Conflict = 5,
    /// <summary>The operation arrived after expiry.</summary>
    Expired = 6,
    /// <summary>The input was malformed.</summary>
    Invalid = 7,
    /// <summary>The target review was not found.</summary>
    NotFound = 8,
    /// <summary>The canonical dependencies were unavailable.</summary>
    Unavailable = 9,
    /// <summary>The operation quota was exhausted.</summary>
    LimitExceeded = 10
}
