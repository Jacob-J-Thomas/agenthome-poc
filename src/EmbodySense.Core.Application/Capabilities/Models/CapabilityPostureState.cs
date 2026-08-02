namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Summarizes the current non-authorizing posture of one exact capability.</summary>
public enum CapabilityPostureState
{
    /// <summary>The capability is current, compatible, healthy, and free of known dependency conflicts.</summary>
    Available = 1,

    /// <summary>The capability remains visible with degraded or recovered evidence.</summary>
    Degraded = 2,

    /// <summary>The capability cannot currently be treated as usable.</summary>
    Unavailable = 3,

    /// <summary>The exact capability does not support the current host contract or platform.</summary>
    Incompatible = 4,

    /// <summary>Current dependent or lifecycle evidence conflicts with the exact capability version.</summary>
    DependencyConflict = 5,

    /// <summary>The capability is tombstoned while retained identity and provenance remain inspectable.</summary>
    Removed = 6
}
