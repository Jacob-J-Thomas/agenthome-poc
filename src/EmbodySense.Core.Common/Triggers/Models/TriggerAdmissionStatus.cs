namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Identifies a visible, non-executing trigger admission outcome.
/// </summary>
public enum TriggerAdmissionStatus
{
    /// <summary>No supported status is present.</summary>
    Unknown = 0,
    /// <summary>The delivery was admitted as evidence only.</summary>
    Admitted = 1,
    /// <summary>An exact prior admission was replayed.</summary>
    Replayed = 2,
    /// <summary>An identity was reused with different canonical content.</summary>
    Conflicting = 3,
    /// <summary>The delivery has not reached its eligibility instant.</summary>
    NotYetEligible = 4,
    /// <summary>The delivery deadline or expiry has passed.</summary>
    Expired = 5,
    /// <summary>Current authority evidence does not permit admission.</summary>
    Unauthorized = 6,
    /// <summary>A required current dependency is unavailable.</summary>
    Unavailable = 7,
    /// <summary>The supplied evidence is invalid.</summary>
    Invalid = 8
}
