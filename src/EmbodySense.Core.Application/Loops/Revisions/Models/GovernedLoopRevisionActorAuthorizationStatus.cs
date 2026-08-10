namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Identifies a server-owned actor authorization decision for one exact lifecycle request hash.</summary>
public enum GovernedLoopRevisionActorAuthorizationStatus
{
    /// <summary>No trustworthy decision was supplied.</summary>
    Unknown = 0,
    /// <summary>The actor is authorized for the exact bound request.</summary>
    Authorized = 1,
    /// <summary>The actor is not authorized for the exact bound request.</summary>
    Denied = 2,
    /// <summary>Current authority evidence could not be evaluated.</summary>
    Unavailable = 3,
}
