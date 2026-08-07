namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Preserves optional artifact integrity and signature evidence without asserting trust.</summary>
public sealed record CapabilityDependencyArtifactMetadata(CapabilityIntegrityDigest? Checksum, string? Signature);
