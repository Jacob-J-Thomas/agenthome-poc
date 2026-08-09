namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether lifecycle history was read from current or recovered proved state.</summary>
public enum CapabilityLifecycleReadStatus
{
    /// <summary>The current authenticated aggregate was read.</summary>
    Available = 1,
    /// <summary>The last proved aggregate was recovered read-only.</summary>
    RecoveredLastProved = 2,
    /// <summary>The capability is unknown.</summary>
    NotFound = 3,
    /// <summary>No trustworthy aggregate can be read.</summary>
    Unavailable = 4
}
