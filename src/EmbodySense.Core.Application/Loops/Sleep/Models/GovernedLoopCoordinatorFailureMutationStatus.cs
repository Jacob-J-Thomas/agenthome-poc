namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed failure compare-and-swap outcome.</summary>
public enum GovernedLoopCoordinatorFailureMutationStatus
{
    /// <summary>The contiguous failure successor was appended.</summary>
    Appended = 1,

    /// <summary>The exact proposed failure was already appended.</summary>
    Duplicate = 2,

    /// <summary>The expected ownership is no longer authoritative.</summary>
    OwnershipLost = 3,

    /// <summary>The expected prior failure head no longer matches.</summary>
    Conflict = 4,

    /// <summary>Retained evidence was malformed, inconsistent, or corrupt.</summary>
    Corrupt = 5,

    /// <summary>The durable evidence source was unavailable.</summary>
    Unavailable = 6
}
