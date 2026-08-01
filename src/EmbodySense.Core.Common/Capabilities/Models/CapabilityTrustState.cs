namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Identifies server-owned verification posture separately from provenance metadata.</summary>
public enum CapabilityTrustState
{
    /// <summary>The capability has not been verified.</summary>
    Unverified = 0,

    /// <summary>The server verified the capability against configured expectations.</summary>
    Verified = 1,

    /// <summary>The server rejected the capability's trust evidence.</summary>
    Rejected = 2
}
