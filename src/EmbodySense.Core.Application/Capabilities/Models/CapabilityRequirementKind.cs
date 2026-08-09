namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether a dependent may continue without one capability.</summary>
public enum CapabilityRequirementKind
{
    /// <summary>The dependent must fail closed when the requirement cannot be preserved.</summary>
    Required = 1,
    /// <summary>The dependent may remain visible in an explicitly degraded posture.</summary>
    Optional = 2
}
