using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Preserves the exact descriptor, implementation, and provenance evidence selected by resolution.</summary>
public sealed record CapabilityResolvedPin(CapabilityDescriptorIdentity DescriptorIdentity, CapabilityImplementationIdentity Implementation, CapabilityProvenance Provenance, CapabilityDependencyArtifactMetadata Artifact);
