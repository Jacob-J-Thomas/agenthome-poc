namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies the structured reason a model profile cannot be dispatched.</summary>
public enum ModelProfileAvailabilityReason
{
    /// <summary>The profile is ready under current evidence.</summary>
    Ready = 1,
    /// <summary>Safe domain metadata is absent or inconsistent.</summary>
    MetadataUnavailable = 2,
    /// <summary>The shared capability is not declared, installed, or enabled.</summary>
    LifecycleUnavailable = 3,
    /// <summary>The shared capability is not verified.</summary>
    TrustUnavailable = 4,
    /// <summary>The shared capability is degraded or unavailable.</summary>
    HealthUnavailable = 5,
    /// <summary>The shared capability is deprecated or removed.</summary>
    Retired = 6,
    /// <summary>The exact adapter/configuration is unregistered or incompatible.</summary>
    AdapterUnavailable = 7,
    /// <summary>The current evidence is malformed or contradictory.</summary>
    EvidenceUnavailable = 8
}
