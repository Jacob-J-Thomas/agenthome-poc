using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Projects one safe capability-backed model profile and current structured availability.</summary>
/// <param name="ProfileId">The exact generic capability identity.</param>
/// <param name="Metadata">The validated safe profile metadata when available.</param>
/// <param name="Reason">The current availability reason.</param>
/// <param name="CapabilityCatalogRevision">The exact shared capability catalog revision.</param>
/// <param name="AdapterRegistryRevisionHash">The safe exact adapter registry revision hash when available.</param>
/// <param name="ProfileSourceRevisionHash">The exact server-owned metadata-source revision when validated metadata is projected.</param>
/// <param name="CapabilityPin">The exact non-granting current capability pin when the catalog entry is structurally valid.</param>
public sealed record ModelProfileCatalogItem(
    CapabilityId ProfileId,
    GovernedModelProfileMetadata? Metadata,
    ModelProfileAvailabilityReason Reason,
    long CapabilityCatalogRevision,
    string? AdapterRegistryRevisionHash,
    string? ProfileSourceRevisionHash = null,
    CapabilityAdmissionPin? CapabilityPin = null);
