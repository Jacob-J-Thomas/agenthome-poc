namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed atomic coordinator-acquisition outcome.</summary>
public enum GovernedLoopCoordinatorAcquisitionStatus
{
    /// <summary>The ownership, starting lifecycle, and initial heartbeat were committed atomically.</summary>
    Acquired = 1,

    /// <summary>The exact proposed evidence was already committed.</summary>
    Duplicate = 2,

    /// <summary>A different live owner currently holds the coordinator.</summary>
    OwnedByLivePeer = 3,

    /// <summary>The matched prior owner's exclusive lease has not expired.</summary>
    LeaseNotExpired = 4,

    /// <summary>The expected prior evidence no longer matches.</summary>
    Conflict = 5,

    /// <summary>Retained evidence was malformed, inconsistent, or corrupt.</summary>
    Corrupt = 6,

    /// <summary>The durable evidence source was unavailable.</summary>
    Unavailable = 7
}
