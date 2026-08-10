namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies exact grant lifecycle, time, and dependency posture.</summary>
public enum AuthorityGrantResolutionStatus
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact grant and every dependency are active at trusted time.</summary>
    Active = 1,
    /// <summary>The exact grant is current but not yet effective.</summary>
    NotEffective = 2,
    /// <summary>The exact grant is suspended.</summary>
    Suspended = 3,
    /// <summary>The exact grant is terminally revoked.</summary>
    Revoked = 4,
    /// <summary>The exact grant is terminally or temporally expired.</summary>
    Expired = 5,
    /// <summary>The exact immutable grant revision exists but is no longer current.</summary>
    Stale = 6,
    /// <summary>The exact profile dependency is not active and matching.</summary>
    ProfileUnavailable = 7,
    /// <summary>The exact role dependency is not active and matching.</summary>
    RoleUnavailable = 8,
    /// <summary>The exact loop publication or owner binding is not active and matching.</summary>
    LoopUnavailable = 9,
    /// <summary>The requested ceiling exceeds an exact dependency maximum.</summary>
    CeilingExceeded = 10,
    /// <summary>The exact grant was not found.</summary>
    NotFound = 11,
    /// <summary>The supplied exact reference is invalid.</summary>
    Invalid = 12,
    /// <summary>Trusted dependency or store state is unavailable.</summary>
    Unavailable = 13,
    /// <summary>Durable evidence could not prove one consistent posture.</summary>
    Ambiguous = 14,
}
