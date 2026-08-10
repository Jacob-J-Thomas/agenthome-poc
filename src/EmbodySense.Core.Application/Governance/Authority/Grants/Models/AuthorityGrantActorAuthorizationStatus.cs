namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies current server-owned actor authorization for one exact grant mutation.</summary>
public enum AuthorityGrantActorAuthorizationStatus
{
    /// <summary>No supported decision was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact actor and request are currently authorized.</summary>
    Authorized = 1,
    /// <summary>The exact actor and request are denied.</summary>
    Denied = 2,
    /// <summary>Current authorization could not be established.</summary>
    Unavailable = 3,
}
