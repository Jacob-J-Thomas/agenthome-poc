namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the externally observable result of one authenticated Human Review decision operation.</summary>
public enum HumanReviewDecisionServiceStatus
{
    /// <summary>The operation was accepted.</summary>
    Accepted,
    /// <summary>An information request was accepted.</summary>
    InformationRequested,
    /// <summary>Authentication failed, or the authenticated caller was ineligible for the exact request.</summary>
    Denied,
    /// <summary>The request or operation conflicted with durable state.</summary>
    Conflict,
    /// <summary>The request expired before acceptance.</summary>
    Expired,
    /// <summary>An authorized exact operation replay was returned.</summary>
    Replayed,
    /// <summary>The run was not found.</summary>
    NotFound,
    /// <summary>The input or durable state was invalid.</summary>
    Invalid,
    /// <summary>A trusted dependency was unavailable.</summary>
    Unavailable,
    /// <summary>The durable store rejected the bounded mutation for capacity.</summary>
    LimitExceeded
}
