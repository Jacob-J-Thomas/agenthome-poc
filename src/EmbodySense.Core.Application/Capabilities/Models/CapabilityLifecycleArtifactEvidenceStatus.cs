namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether an immutable lifecycle target is staged and proved.</summary>
public enum CapabilityLifecycleArtifactEvidenceStatus
{
    /// <summary>The exact descriptor and artifact digest are staged under server-owned trust.</summary>
    Proved = 1,
    /// <summary>No matching immutable target exists.</summary>
    NotFound = 2,
    /// <summary>Artifact evidence cannot be proved safely.</summary>
    Unavailable = 3
}
