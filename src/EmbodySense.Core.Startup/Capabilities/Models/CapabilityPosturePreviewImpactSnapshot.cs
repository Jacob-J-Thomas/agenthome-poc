namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Projects one deterministic read-only dependent impact.</summary>
/// <param name="Dependent">The safe dependent requirement.</param>
/// <param name="IsCompatible">Whether the target version preserves the requirement.</param>
/// <param name="Outcome">The stable proposed outcome token.</param>
public sealed record CapabilityPosturePreviewImpactSnapshot(CapabilityPostureDependentSnapshot Dependent, bool IsCompatible, string Outcome);
