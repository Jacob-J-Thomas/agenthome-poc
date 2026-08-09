namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies the bounded server-owned lifecycle target resolution outcome.</summary>
public enum CapabilityLifecycleTargetResolutionStatus
{
    /// <summary>Exactly one fully proved target matched.</summary>
    Available = 1,
    /// <summary>A complete proved scan found no matching target.</summary>
    NotFound = 2,
    /// <summary>More than one distinct proved target matched and no lexical choice was made.</summary>
    Ambiguous = 3,
    /// <summary>The complete target set could not be proved safely.</summary>
    Unavailable = 4
}
