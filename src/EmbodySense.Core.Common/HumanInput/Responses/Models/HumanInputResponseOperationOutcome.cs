namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Identifies one immutable terminal response-operation disposition.</summary>
public enum HumanInputResponseOperationOutcome
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact response mutation committed, whether the request remained pending or became answered.</summary>
    Committed = 1,
    /// <summary>Exact optimistic, duplicate, stale, or selection state conflicted without mutation.</summary>
    Conflict = 2,
    /// <summary>Trusted validation, timing, eligibility, or terminal posture rejected the operation without mutation.</summary>
    Rejected = 3,
    /// <summary>The exact request or response target did not exist.</summary>
    NotFound = 4,
    /// <summary>A finite schema-1 response or evidence bound was exhausted.</summary>
    LimitExceeded = 5
}
