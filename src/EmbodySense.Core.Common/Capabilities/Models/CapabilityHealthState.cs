namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Identifies observed runtime health separately from installation and enablement.</summary>
public enum CapabilityHealthState
{
    /// <summary>Health has not been established.</summary>
    Unknown = 0,

    /// <summary>The capability is healthy.</summary>
    Healthy = 1,

    /// <summary>The capability is available with degraded behavior.</summary>
    Degraded = 2,

    /// <summary>The capability is unavailable.</summary>
    Unavailable = 3
}
