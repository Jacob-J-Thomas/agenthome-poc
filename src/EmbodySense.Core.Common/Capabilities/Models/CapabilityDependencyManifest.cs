namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Declares closed schema-version-1 required and optional capability dependencies.</summary>
/// <remarks>The manifest is dependency metadata only. It cannot grant trust, permissions, secrets, execution, loop assignment, or authority.</remarks>
public sealed record CapabilityDependencyManifest(
    int SchemaVersion,
    CapabilityDependencyManifestKind Kind,
    CapabilityId SubjectId,
    IReadOnlyList<CapabilityDependency> Required,
    IReadOnlyList<CapabilityDependency> Optional,
    CapabilityDependencyArtifactMetadata Artifact)
{
    /// <summary>Gets the only supported experimental dependency-manifest schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
