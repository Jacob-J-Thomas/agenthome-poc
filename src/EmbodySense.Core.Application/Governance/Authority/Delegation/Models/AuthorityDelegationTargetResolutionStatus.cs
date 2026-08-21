namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Classifies exact delegation-target resolution.</summary>
public enum AuthorityDelegationTargetResolutionStatus
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact immutable target and every maximum remain active.</summary>
    Active = 1,
    /// <summary>The exact target is stale or replaced.</summary>
    Stale = 2,
    /// <summary>The exact target is disabled.</summary>
    Disabled = 3,
    /// <summary>The exact target was not found.</summary>
    NotFound = 4,
    /// <summary>The supplied target is malformed.</summary>
    Invalid = 5,
    /// <summary>Target truth is unavailable.</summary>
    Unavailable = 6,
    /// <summary>Target evidence cannot prove one posture.</summary>
    Ambiguous = 7,
}
