namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Preserves the exact immutable capability identity selected when a loop was admitted.</summary>
/// <param name="DescriptorIdentity">The exact identifier, version, and canonical descriptor hash.</param>
/// <param name="Kind">The closed capability kind.</param>
/// <param name="Implementation">The exact provider and implementation identity.</param>
/// <param name="Provenance">The exact safe provenance evidence.</param>
/// <param name="Artifact">The exact optional artifact integrity evidence.</param>
/// <param name="SafeDescription">The bounded public purpose text that may be projected into model context.</param>
/// <remarks>This is compatibility and provenance evidence, not permission or authority.</remarks>
public sealed record CapabilityAdmissionPin(
    CapabilityDescriptorIdentity DescriptorIdentity,
    CapabilityKind Kind,
    CapabilityImplementationIdentity Implementation,
    CapabilityProvenance Provenance,
    CapabilityDependencyArtifactMetadata Artifact,
    string SafeDescription);
