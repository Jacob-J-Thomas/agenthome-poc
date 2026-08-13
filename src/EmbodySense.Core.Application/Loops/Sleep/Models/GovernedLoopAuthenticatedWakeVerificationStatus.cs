namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies the closed outcome of trusted authenticated-event verification.</summary>
public enum GovernedLoopAuthenticatedWakeVerificationStatus
{
    /// <summary>The exact evidence, chronology, and eligibility were authoritatively verified.</summary>
    Verified = 1,

    /// <summary>The submitted evidence was conclusively rejected as forged or otherwise unauthenticated.</summary>
    Rejected = 2,

    /// <summary>No authoritative authentication evidence exists for the submitted hash.</summary>
    NotFound = 3,

    /// <summary>Conflicting authoritative authentication evidence prevented verification.</summary>
    Conflict = 4,

    /// <summary>The authoritative verification source was unavailable.</summary>
    Unavailable = 5
}
