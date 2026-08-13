namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed coordinator-evidence read outcome.</summary>
public enum GovernedLoopCoordinatorReadStatus
{
    /// <summary>Validated current evidence was found.</summary>
    Found = 1,

    /// <summary>No coordinator evidence exists.</summary>
    NotFound = 2,

    /// <summary>Retained evidence was malformed, inconsistent, or corrupt.</summary>
    Corrupt = 3,

    /// <summary>The durable evidence source was unavailable.</summary>
    Unavailable = 4
}
