namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Identifies one closed durable operational-control outcome.</summary>
public enum GovernedLoopOperationalControlStatus
{
    /// <summary>The mutation and durable terminal receipt completed.</summary>
    Applied = 1,

    /// <summary>The exact durable terminal outcome was replayed.</summary>
    Replayed = 2,

    /// <summary>The same operation identity is currently owned by another executor.</summary>
    OperationInProgress = 3,

    /// <summary>The optimistic target or request identity conflicted.</summary>
    Conflict = 4,

    /// <summary>The target was not found.</summary>
    NotFound = 5,

    /// <summary>Current trusted authority did not admit the control.</summary>
    Unauthorized = 6,

    /// <summary>The current lifecycle does not admit the requested control.</summary>
    Ineligible = 7,

    /// <summary>A bounded batch retained partial progress and requires reconciliation.</summary>
    PartiallyApplied = 8,

    /// <summary>Durable capacity prevented admission or completion.</summary>
    Backpressured = 9,

    /// <summary>Retained evidence was malformed or contradictory.</summary>
    Corrupt = 10,

    /// <summary>An authoritative source was unavailable.</summary>
    Unavailable = 11,

    /// <summary>The public request was malformed or outside finite bounds.</summary>
    Invalid = 12,

    /// <summary>Ambiguous evidence requires explicit operator review.</summary>
    NeedsReview = 13
}
