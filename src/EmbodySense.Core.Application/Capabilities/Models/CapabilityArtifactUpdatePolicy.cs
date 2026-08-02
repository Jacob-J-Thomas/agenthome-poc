namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Describes when a caller may replace an activated artifact without performing that update.</summary>
public enum CapabilityArtifactUpdatePolicy
{
    /// <summary>The update policy is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The exact source revision is permanently pinned.</summary>
    Pinned = 1,
    /// <summary>A separate explicit intake operation is required for every update.</summary>
    Manual = 2
}
