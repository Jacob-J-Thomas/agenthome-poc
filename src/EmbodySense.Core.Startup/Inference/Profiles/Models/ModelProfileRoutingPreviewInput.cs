using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles.Models;

/// <summary>Supplies non-authoritative authoring intent for a server-recomputed model-routing preview.</summary>
public sealed record ModelProfileRoutingPreviewInput(
    GovernedModelRoutingPolicy Policy,
    string RoleId,
    string NodeTypeId,
    IReadOnlyList<string>? AuthoredInputDataClasses);
