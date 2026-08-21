using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles.Models;

/// <summary>Returns exact current catalog pins and constraints without granting admission or dispatch authority.</summary>
public sealed record ModelProfileRoutingPreviewResponse(
    string Status,
    string Reason,
    string? PolicyHash,
    string? ResolvedDefaultProfileId,
    GovernedModelProfilePin? Primary,
    IReadOnlyList<GovernedModelProfilePin> Fallbacks,
    GovernedModelProfileRequirements? Requirements,
    bool AdmissionRequired);
