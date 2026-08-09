using EmbodySense.Core.Startup.Capabilities.Models;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Defines the surface-neutral safe capability catalog and lifecycle boundary.</summary>
public interface ICapabilityCatalogFacade
{
    /// <summary>Reads one bounded deterministic page of safe capability posture.</summary>
    /// <param name="startAfterId">The optional exclusive capability cursor.</param>
    /// <param name="maximumCount">The requested page size.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>The safe catalog page.</returns>
    Task<CapabilityPostureCatalogResponse> ReadCatalogAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact safe capability posture.</summary>
    /// <param name="capabilityId">The canonical capability identity.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>The safe posture response.</returns>
    Task<CapabilityPostureResponse> ReadAsync(string capabilityId, CancellationToken cancellationToken = default);

    /// <summary>Creates or exactly replays one server-owned durable lifecycle preview.</summary>
    /// <param name="input">The bounded client selection.</param>
    /// <param name="cancellationToken">The token used to cancel preview creation.</param>
    /// <returns>The safe preview projection.</returns>
    Task<CapabilityLifecyclePreviewResponse> PreviewAsync(CapabilityLifecycleSelectionInput input, CancellationToken cancellationToken = default);

    /// <summary>Confirms one exact preview after checking every caller-observed concurrency identity.</summary>
    /// <param name="input">The explicit confirmation and exact expected preview identities.</param>
    /// <param name="cancellationToken">The token used to cancel before the durable terminal boundary.</param>
    /// <returns>The safe mutation outcome.</returns>
    Task<CapabilityLifecycleMutationResponse> ConfirmAsync(CapabilityLifecycleConfirmationInput input, CancellationToken cancellationToken = default);
}
