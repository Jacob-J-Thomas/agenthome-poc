namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Retains the current authority posture relevant to a failure without granting authority.</summary>
public enum GovernedLoopFailureAuthorityPosture
{
    /// <summary>No trustworthy authority posture is available.</summary>
    Unknown = 0,
    /// <summary>Authority is not relevant to this observation.</summary>
    NotApplicable,
    /// <summary>Current authority was proved sufficient for the attempted boundary.</summary>
    Current,
    /// <summary>Current authority denied the boundary.</summary>
    Denied,
    /// <summary>Previously admitted authority was revoked or narrowed.</summary>
    Revoked,
}
