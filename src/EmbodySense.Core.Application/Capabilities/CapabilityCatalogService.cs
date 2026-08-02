using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Exposes explicit catalog queries and lifecycle transitions over the persistence port.</summary>
public sealed class CapabilityCatalogService
{
    private readonly ICapabilityCatalogStore _store;

    /// <summary>Creates the service over a catalog persistence port.</summary>
    /// <param name="store">The persistence port.</param>
    public CapabilityCatalogService(ICapabilityCatalogStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Reads one bounded catalog page.</summary>
    public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default) => _store.ReadAsync(startAfterId, maximumCount, cancellationToken);

    /// <summary>Declares a capability without installing, enabling, trusting, assigning, or authorizing it.</summary>
    public Task<CapabilityCatalogMutationResult> DeclareAsync(CapabilityDescriptor descriptor, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return MutateAsync(CapabilityCatalogMutationKind.Declare, descriptor.Id, descriptor, expectedCatalogRevision, operationId, cancellationToken);
    }

    /// <summary>Marks a declared capability implementation installed.</summary>
    public Task<CapabilityCatalogMutationResult> InstallAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.Install, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Marks a capability enabled without assigning it to any loop or role.</summary>
    public Task<CapabilityCatalogMutationResult> EnableAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.Enable, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Marks a capability disabled.</summary>
    public Task<CapabilityCatalogMutationResult> DisableAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.Disable, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Records the server's verified trust decision.</summary>
    public Task<CapabilityCatalogMutationResult> VerifyAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.Verify, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Records the server's rejected trust decision.</summary>
    public Task<CapabilityCatalogMutationResult> RejectTrustAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.RejectTrust, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Records a healthy observation.</summary>
    public Task<CapabilityCatalogMutationResult> MarkHealthyAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.MarkHealthy, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Records a degraded observation.</summary>
    public Task<CapabilityCatalogMutationResult> MarkDegradedAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.MarkDegraded, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Records an unavailable observation.</summary>
    public Task<CapabilityCatalogMutationResult> MarkUnavailableAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.MarkUnavailable, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Marks a capability deprecated without removing its evidence.</summary>
    public Task<CapabilityCatalogMutationResult> DeprecateAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.Deprecate, id, null, expectedCatalogRevision, operationId, cancellationToken);

    /// <summary>Removes a capability while retaining its tombstone and provenance evidence.</summary>
    public Task<CapabilityCatalogMutationResult> RemoveAsync(CapabilityId id, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken = default) => MutateAsync(CapabilityCatalogMutationKind.Remove, id, null, expectedCatalogRevision, operationId, cancellationToken);

    private Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutationKind kind, CapabilityId id, CapabilityDescriptor? descriptor, long expectedCatalogRevision, string operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _store.MutateAsync(new CapabilityCatalogMutation(kind, operationId, expectedCatalogRevision, id, descriptor), cancellationToken);
    }
}
