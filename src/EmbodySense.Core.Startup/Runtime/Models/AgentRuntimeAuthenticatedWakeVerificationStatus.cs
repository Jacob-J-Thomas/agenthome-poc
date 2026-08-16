namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Identifies the closed surface-owned authenticated-event verification outcome.</summary>
public enum AgentRuntimeAuthenticatedWakeVerificationStatus
{
    /// <summary>The exact evidence, chronology, and eligibility were verified.</summary>
    Verified = 1,

    /// <summary>The evidence was conclusively rejected as unauthenticated.</summary>
    Rejected = 2,

    /// <summary>No authoritative evidence exists for the submitted hash.</summary>
    NotFound = 3,

    /// <summary>Conflicting authoritative evidence prevented verification.</summary>
    Conflict = 4,

    /// <summary>The authoritative verification source was unavailable.</summary>
    Unavailable = 5
}
