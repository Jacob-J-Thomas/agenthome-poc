using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects dependency metadata from one currently activated immutable capability package.</summary>
/// <param name="CapabilityId">The activated package identity.</param>
/// <param name="ArtifactDigest">The exact immutable package digest used as its revision.</param>
/// <param name="Manifest">The validated package dependency manifest.</param>
public sealed record CapabilityPackageDependencyDiscovery(string CapabilityId, string ArtifactDigest, CapabilityDependencyManifest Manifest);
