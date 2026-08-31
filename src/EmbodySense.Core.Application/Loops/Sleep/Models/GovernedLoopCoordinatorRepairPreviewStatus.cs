namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies whether one current coordinator repair preview may be submitted.</summary>
public enum GovernedLoopCoordinatorRepairPreviewStatus
{
    /// <summary>The exact failed evidence, authority, lease, and all dependency readiness checks are current.</summary>
    Ready = 1,

    /// <summary>The supplied operation identity or coordinator identity was malformed.</summary>
    Invalid = 2,

    /// <summary>Current authenticated operator authority did not permit the repair.</summary>
    Unauthorized = 3,

    /// <summary>No coordinator evidence exists.</summary>
    NotFound = 4,

    /// <summary>Current evidence is not an expired failed coordinator generation eligible for repair.</summary>
    Conflict = 5,

    /// <summary>Evidence or a dependency reply was malformed or ambiguous.</summary>
    Corrupt = 6,

    /// <summary>An authority, clock, coordinator, or dependency source was unavailable.</summary>
    Unavailable = 7
}
