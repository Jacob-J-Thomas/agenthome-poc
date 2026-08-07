namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether a catalog read used current or last-proved state.</summary>
public enum CapabilityCatalogReadStatus
{
    /// <summary>The current canonical artifact was available, including an absent empty catalog.</summary>
    Available = 1,

    /// <summary>The primary artifact was unsafe and the last proved artifact was returned read-only.</summary>
    RecoveredLastProved = 2,

    /// <summary>No trustworthy catalog state could be read.</summary>
    Unavailable = 3
}
