namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines the closed schema-1 Human Review decision vocabulary.</summary>
public enum HumanReviewDecisionKind
{
    /// <summary>No supported decision was supplied.</summary>
    Unknown = 0,
    /// <summary>Consents only to the exact bound continuation or conclusively pre-dispatch effect attempt.</summary>
    Approve = 1,
    /// <summary>Declines the reviewed work through its authored failure route or terminal failure.</summary>
    Reject = 2,
    /// <summary>Invokes the existing exact run cancellation lifecycle.</summary>
    Cancel = 3,
    /// <summary>Retains the parked frontier and records a bounded request for information without approving work.</summary>
    RequestInformation = 4
}
