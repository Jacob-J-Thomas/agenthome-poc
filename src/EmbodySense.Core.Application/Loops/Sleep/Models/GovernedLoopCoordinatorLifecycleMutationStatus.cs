namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed lifecycle compare-and-swap outcome.</summary>
public enum GovernedLoopCoordinatorLifecycleMutationStatus
{
    /// <summary>The contiguous lifecycle successor was appended.</summary>
    Appended = 1,

    /// <summary>The exact proposed lifecycle was already appended.</summary>
    Duplicate = 2,

    /// <summary>The expected ownership is no longer authoritative.</summary>
    OwnershipLost = 3,

    /// <summary>The expected prior lifecycle no longer matches.</summary>
    Conflict = 4,

    /// <summary>Retained evidence was malformed, inconsistent, or corrupt.</summary>
    Corrupt = 5,

    /// <summary>The durable evidence source was unavailable.</summary>
    Unavailable = 6
}
