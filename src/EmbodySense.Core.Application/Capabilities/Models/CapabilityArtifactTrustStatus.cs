namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies a server-owned artifact verification outcome.</summary>
public enum CapabilityArtifactTrustStatus
{
    /// <summary>The artifact evidence was verified by server-owned policy.</summary>
    Verified = 1,
    /// <summary>The artifact evidence was rejected.</summary>
    Rejected = 2,
    /// <summary>The required trust policy or verification material is unavailable.</summary>
    Unavailable = 3
}
