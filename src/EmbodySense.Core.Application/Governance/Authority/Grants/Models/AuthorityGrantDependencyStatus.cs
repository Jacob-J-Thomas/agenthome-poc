namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies one exact dependency's current posture without selecting a replacement.</summary>
public enum AuthorityGrantDependencyStatus
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact dependency remains current and active.</summary>
    Active = 1,
    /// <summary>The exact dependency is current but disabled or otherwise inactive.</summary>
    Disabled = 2,
    /// <summary>The exact dependency is past its trusted expiry boundary.</summary>
    Expired = 3,
    /// <summary>The exact immutable dependency exists but is no longer current.</summary>
    Stale = 4,
    /// <summary>The exact dependency was not found.</summary>
    NotFound = 5,
    /// <summary>The supplied exact pin was invalid.</summary>
    Invalid = 6,
    /// <summary>The dependency source could not establish trustworthy state.</summary>
    Unavailable = 7,
    /// <summary>Evidence could not prove one consistent dependency posture.</summary>
    Ambiguous = 8,
}
