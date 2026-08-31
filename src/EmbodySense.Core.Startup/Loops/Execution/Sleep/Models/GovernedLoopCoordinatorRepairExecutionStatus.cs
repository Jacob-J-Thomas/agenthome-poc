namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Identifies the surface-neutral coordinator repair and canonical-start outcome.</summary>
public enum GovernedLoopCoordinatorRepairExecutionStatus
{
    /// <summary>The repair was appended and this runtime entered the canonical ready coordinator lifetime.</summary>
    Repaired = 1,

    /// <summary>The same exact repair and its retained canonical startup outcome were replayed.</summary>
    Replayed = 2,

    /// <summary>The submitted repair binding was malformed.</summary>
    Invalid = 3,

    /// <summary>The current authenticated runtime operator was not authorized.</summary>
    Unauthorized = 4,

    /// <summary>Current evidence, a live peer, a renewed lease, or a changed dependency prevented restart.</summary>
    Conflict = 5,

    /// <summary>Evidence could not establish one safe repair outcome.</summary>
    Corrupt = 6,

    /// <summary>A required authority, dependency, ledger, or host source was unavailable.</summary>
    Unavailable = 7
}
