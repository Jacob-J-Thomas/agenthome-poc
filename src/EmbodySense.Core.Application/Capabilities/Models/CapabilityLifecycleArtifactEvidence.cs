namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns whether an exact immutable artifact is eligible to become a lifecycle target.</summary>
/// <param name="Status">The evidence outcome.</param>
/// <param name="Detail">A bounded operator-facing explanation.</param>
public sealed record CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus Status, string Detail);
