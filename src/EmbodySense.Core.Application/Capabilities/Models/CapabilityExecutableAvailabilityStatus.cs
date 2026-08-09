namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether every declared executable boundary can be enforced.</summary>
public enum CapabilityExecutableAvailabilityStatus
{
    /// <summary>All declared boundaries are enforceable by the configured host.</summary>
    Available = 1,
    /// <summary>The artifact is incompatible with the current host.</summary>
    Incompatible = 2,
    /// <summary>A required isolation or brokerage control is unavailable.</summary>
    Unavailable = 3
}
