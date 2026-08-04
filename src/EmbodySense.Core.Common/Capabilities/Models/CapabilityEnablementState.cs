namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Identifies server-owned enablement without assigning a capability to a loop.</summary>
public enum CapabilityEnablementState
{
    /// <summary>The enablement state is unknown.</summary>
    Unknown = 0,

    /// <summary>The capability is disabled.</summary>
    Disabled = 1,

    /// <summary>The capability is enabled but has no implied assignment or authority.</summary>
    Enabled = 2
}
