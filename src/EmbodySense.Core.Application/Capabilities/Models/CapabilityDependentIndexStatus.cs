namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether every registered dependent source was captured safely.</summary>
public enum CapabilityDependentIndexStatus
{
    /// <summary>The complete bounded dependent set is available.</summary>
    Available = 1,
    /// <summary>At least one source or entry could not be proved, so mutations must fail closed.</summary>
    Unavailable = 2
}
