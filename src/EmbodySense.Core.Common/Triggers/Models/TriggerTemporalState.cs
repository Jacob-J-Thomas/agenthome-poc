namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Identifies the exact temporal state of a structurally valid trigger delivery.
/// </summary>
public enum TriggerTemporalState
{
    /// <summary>The temporal state is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The delivery is before its inclusive not-before instant.</summary>
    NotYetEligible = 1,
    /// <summary>The delivery is eligible at the evaluated instant.</summary>
    Eligible = 2,
    /// <summary>The inclusive deadline has been exceeded.</summary>
    DeadlineExceeded = 3,
    /// <summary>The exclusive-validity expiry instant has been reached.</summary>
    Expired = 4
}
