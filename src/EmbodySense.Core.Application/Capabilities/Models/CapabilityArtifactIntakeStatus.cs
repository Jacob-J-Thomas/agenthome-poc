namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies a capability artifact intake outcome.</summary>
public enum CapabilityArtifactIntakeStatus
{
    /// <summary>The verified artifact became the active revision.</summary>
    Activated = 1,
    /// <summary>The exact durable operation was replayed.</summary>
    Replayed = 2,
    /// <summary>The request or manifest was invalid.</summary>
    Invalid = 3,
    /// <summary>Source bytes did not match declared integrity evidence.</summary>
    IntegrityRejected = 4,
    /// <summary>Server-owned trust policy rejected the artifact.</summary>
    TrustRejected = 5,
    /// <summary>The host platform is incompatible.</summary>
    Incompatible = 6,
    /// <summary>Declared requirements cannot be enforced.</summary>
    RequirementsUnavailable = 7,
    /// <summary>The optimistic activation revision was stale.</summary>
    Conflict = 8,
    /// <summary>Source, trust, staging, or activation infrastructure was unavailable.</summary>
    Unavailable = 9
}
