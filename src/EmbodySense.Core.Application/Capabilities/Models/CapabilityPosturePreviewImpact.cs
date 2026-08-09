namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one deterministic read-only dependent impact.</summary>
/// <param name="Dependent">The safe dependent requirement.</param>
/// <param name="IsCompatible">Whether the target version preserves the requirement.</param>
/// <param name="Outcome">The honest proposed outcome.</param>
public sealed record CapabilityPosturePreviewImpact(CapabilityPostureDependentProjection Dependent, bool IsCompatible, CapabilityLifecycleImpactOutcome Outcome);
