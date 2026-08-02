namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Preserves one bounded resolver decision captured at loop admission.</summary>
public sealed record CapabilityAdmissionEvidence(
    CapabilityId SubjectId,
    CapabilityId DependencyId,
    CapabilityVersionRange CompatibleVersionRange,
    bool IsOptional,
    string Outcome,
    CapabilityDescriptorIdentity? SelectedIdentity,
    string Detail);
