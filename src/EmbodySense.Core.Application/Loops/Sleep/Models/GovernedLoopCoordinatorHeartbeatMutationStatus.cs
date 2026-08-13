namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed heartbeat compare-and-swap outcome.</summary>
public enum GovernedLoopCoordinatorHeartbeatMutationStatus
{
    /// <summary>The contiguous heartbeat successor was committed.</summary>
    Renewed = 1,

    /// <summary>The exact proposed heartbeat was already committed.</summary>
    Duplicate = 2,

    /// <summary>The expected ownership is no longer authoritative.</summary>
    OwnershipLost = 3,

    /// <summary>The expected prior heartbeat no longer matches.</summary>
    Conflict = 4,

    /// <summary>Retained evidence was malformed, inconsistent, or corrupt.</summary>
    Corrupt = 5,

    /// <summary>The durable evidence source was unavailable.</summary>
    Unavailable = 6
}
