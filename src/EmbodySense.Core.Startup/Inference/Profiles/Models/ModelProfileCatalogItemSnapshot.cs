using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles.Models;

/// <summary>Projects one safe capability-backed model profile and exact authoring template.</summary>
public sealed record ModelProfileCatalogItemSnapshot(
    string ProfileId,
    GovernedModelProfileMetadata? Metadata,
    string AvailabilityReason,
    long CapabilityCatalogRevision,
    string? AdapterRegistryRevisionHash,
    string? ProfileSourceRevisionHash,
    GovernedModelRoutingPolicy? RecommendedExactPolicy,
    GovernedModelProfilePin? ExactProfilePin);
