using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Persists and queries one workspace's governed capability catalog.</summary>
public interface ICapabilityCatalogStore
{
    /// <summary>Reads one bounded page without conferring assignment or authority.</summary>
    /// <param name="startAfterId">The optional exclusive canonical identifier cursor.</param>
    /// <param name="maximumCount">The requested page size from one through the store limit.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current or recovered catalog page.</returns>
    Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Applies one idempotent optimistic lifecycle transition.</summary>
    /// <param name="mutation">The mutation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The durable structured outcome.</returns>
    Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default);
}
