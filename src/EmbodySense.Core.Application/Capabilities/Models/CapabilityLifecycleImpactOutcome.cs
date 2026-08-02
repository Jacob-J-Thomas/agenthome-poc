namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies the proposed outcome for one dependent requirement.</summary>
public enum CapabilityLifecycleImpactOutcome
{
    /// <summary>The requirement remains compatible and unchanged.</summary>
    Preserved = 1,
    /// <summary>A required dependency blocks the proposed lifecycle operation.</summary>
    Blocked = 2,
    /// <summary>An optional dependency remains visible with explicit degradation evidence.</summary>
    Degraded = 3
}
