namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Captures immutable requirement, resolution, and workspace-binding evidence for one admitted loop run.</summary>
public sealed record CapabilityAdmissionSnapshot(
    int SchemaVersion,
    string WorkspaceScopeId,
    CapabilityDependencyManifest Requirements,
    string RequirementsHash,
    IReadOnlyList<CapabilityAdmissionPin> Pins,
    IReadOnlyList<CapabilityAdmissionEvidence> Evidence,
    DateTimeOffset AdmittedAtUtc)
{
    /// <summary>Gets the only supported experimental admission schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
