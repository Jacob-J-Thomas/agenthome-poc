using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Combines one governed catalog entry with optional dependency and artifact evidence.</summary>
public sealed record CapabilityDependencyCatalogCandidate(CapabilityCatalogEntry Entry, CapabilityDependencyManifest? Dependencies, CapabilityDependencyArtifactMetadata Artifact);
