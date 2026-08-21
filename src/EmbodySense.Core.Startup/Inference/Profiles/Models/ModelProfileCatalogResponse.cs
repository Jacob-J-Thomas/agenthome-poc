namespace EmbodySense.Core.Startup.Inference.Profiles.Models;

/// <summary>Returns one bounded safe page and exact configured-default identity.</summary>
public sealed record ModelProfileCatalogResponse(
    string Status,
    IReadOnlyList<ModelProfileCatalogItemSnapshot> Profiles,
    string? NextCursor,
    string? DefaultProfileId);
