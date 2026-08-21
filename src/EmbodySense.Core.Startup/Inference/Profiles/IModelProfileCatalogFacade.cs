using EmbodySense.Core.Startup.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Exposes safe bounded model-profile pages from one server-owned composition.</summary>
public interface IModelProfileCatalogFacade
{
    /// <summary>Reads a deterministic current page and configured default.</summary>
    Task<ModelProfileCatalogResponse> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Recomputes exact current catalog eligibility for authoring intent without granting runtime admission.</summary>
    Task<ModelProfileRoutingPreviewResponse> PreviewAsync(ModelProfileRoutingPreviewInput input, CancellationToken cancellationToken = default);
}
