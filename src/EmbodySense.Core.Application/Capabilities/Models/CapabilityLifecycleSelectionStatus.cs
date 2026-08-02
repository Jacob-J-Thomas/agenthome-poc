namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether browser-safe lifecycle selection produced a durable preview.</summary>
public enum CapabilityLifecycleSelectionStatus
{
    /// <summary>A durable ready or replayed preview is available.</summary>
    Ready = 1,
    /// <summary>No proved server-owned target matched.</summary>
    NotFound = 2,
    /// <summary>Multiple proved server-owned targets matched.</summary>
    Ambiguous = 3,
    /// <summary>Selection evidence was unavailable or the durable preview could not be produced safely.</summary>
    Unavailable = 4,
    /// <summary>The browser-safe selection violates the closed contract.</summary>
    Invalid = 5,
    /// <summary>The durable preview conflicts with an existing operation identity or baseline.</summary>
    Conflict = 6
}
