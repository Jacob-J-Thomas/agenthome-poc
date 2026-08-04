namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Identifies deprecation and removal separately from declaration and installation.</summary>
public enum CapabilityRetirementState
{
    /// <summary>The retirement state is unknown.</summary>
    Unknown = 0,

    /// <summary>The capability is active.</summary>
    Active = 1,

    /// <summary>The capability is deprecated but remains addressable.</summary>
    Deprecated = 2,

    /// <summary>The capability has been removed.</summary>
    Removed = 3
}
