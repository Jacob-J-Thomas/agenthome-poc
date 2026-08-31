namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Defines the closed decision vocabulary exposed by the Startup Human Review projection.</summary>
public enum HumanReviewDecisionKind
{
    /// <summary>No supported decision was supplied.</summary>
    Unknown = 0,
    /// <summary>Consents only to the exact bound continuation or pre-dispatch effect.</summary>
    Approve = 1,
    /// <summary>Declines the reviewed work through its authored failure route.</summary>
    Reject = 2,
    /// <summary>Invokes the exact run cancellation lifecycle.</summary>
    Cancel = 3,
    /// <summary>Retains the parked frontier and requests bounded information.</summary>
    RequestInformation = 4
}
