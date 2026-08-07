namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Classifies a bounded local skill discovery observation.</summary>
public enum LocalSkillDependencyDiscoveryStatus
{
    /// <summary>A skill entrypoint and valid manifest were discovered.</summary>
    Discovered = 1,

    /// <summary>The candidate skill has no dependency sidecar and is intentionally omitted.</summary>
    NoManifest = 2,

    /// <summary>The skill or manifest violates a closed contract.</summary>
    Invalid = 3,

    /// <summary>The configured skills scope contains an unsafe or escaping entry.</summary>
    UnsafePath = 4,

    /// <summary>A discovery shape bound was exceeded.</summary>
    LimitExceeded = 5
}
