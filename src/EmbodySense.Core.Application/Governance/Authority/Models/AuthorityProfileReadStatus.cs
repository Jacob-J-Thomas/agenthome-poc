namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Identifies whether a profile read used current, recovered, missing, or unavailable state.</summary>
public enum AuthorityProfileReadStatus
{
    /// <summary>The current profile artifact was proved and available.</summary>
    Available = 1,
    /// <summary>The primary artifact was unsafe and the last proved state is read-only.</summary>
    RecoveredLastProved = 2,
    /// <summary>No profile exists at the requested canonical identifier.</summary>
    NotFound = 3,
    /// <summary>No trustworthy profile state is available.</summary>
    Unavailable = 4
}
