using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Preserves one local skill discovery result and non-authoritative artifact evidence.</summary>
public sealed record LocalSkillDependencyDiscovery(string DirectoryName, LocalSkillDependencyDiscoveryStatus Status, CapabilityDependencyManifest? Manifest, CapabilityDependencyArtifactMetadata? Artifact, string Detail);
