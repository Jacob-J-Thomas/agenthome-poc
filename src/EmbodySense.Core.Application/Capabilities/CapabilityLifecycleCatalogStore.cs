using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Projects current lifecycle aggregate state over catalog declarations so admission has one authoritative runtime view.</summary>
/// <remarks>The underlying catalog retains declaration, installation, health, and trust axes. The lifecycle aggregate exclusively owns replacement descriptor, enablement, and tombstone state after registration.</remarks>
public sealed class CapabilityLifecycleCatalogStore : ICapabilityCatalogStore
{
    private readonly ICapabilityCatalogStore _catalogStore;
    private readonly ICapabilityLifecycleMutationStore _lifecycleStore;

    /// <summary>Creates an admission-facing projection over current proved catalog and lifecycle stores.</summary>
    public CapabilityLifecycleCatalogStore(ICapabilityCatalogStore catalogStore, ICapabilityLifecycleMutationStore lifecycleStore)
    {
        ArgumentNullException.ThrowIfNull(catalogStore);
        ArgumentNullException.ThrowIfNull(lifecycleStore);
        _catalogStore = catalogStore;
        _lifecycleStore = lifecycleStore;
    }

    /// <inheritdoc />
    public async Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        var read = await _catalogStore.ReadAsync(startAfterId, maximumCount, cancellationToken);
        if (read.Status != CapabilityCatalogReadStatus.Available || read.Page is null)
        {
            return read;
        }

        var entries = new List<CapabilityCatalogEntry>(read.Page.Entries.Count);
        foreach (var entry in read.Page.Entries)
        {
            var lifecycle = await _lifecycleStore.ReadAsync(entry.Descriptor.Id, cancellationToken);
            if (lifecycle.Status is CapabilityLifecycleReadStatus.RecoveredLastProved or CapabilityLifecycleReadStatus.Unavailable)
            {
                return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "Current lifecycle state is unproved; the admission catalog fails closed.");
            }
            if (lifecycle.Status == CapabilityLifecycleReadStatus.NotFound)
            {
                entries.Add(entry);
                continue;
            }
            if (lifecycle.State is not { } state || !CapabilityDescriptorIdentity.TryCreate(state.Descriptor, out var descriptorIdentity, out _))
            {
                return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "Current lifecycle state is incomplete or malformed; the admission catalog fails closed.");
            }

            var projectedLifecycle = entry.Lifecycle with
            {
                DescriptorIdentity = descriptorIdentity!,
                Enablement = state.IsEnabled && !state.IsRemoved ? CapabilityEnablementState.Enabled : CapabilityEnablementState.Disabled,
                Retirement = state.IsRemoved ? CapabilityRetirementState.Removed : entry.Lifecycle.Retirement
            };
            entries.Add(new CapabilityCatalogEntry(state.Descriptor, projectedLifecycle, Math.Max(entry.Revision, state.Revision), state.UpdatedAtUtc, state.LastOperationId));
        }

        return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(read.Page.CatalogRevision, entries, read.Page.NextCursor), "The current proved catalog and lifecycle state were projected without duplicating mutable truth.");
    }

    /// <inheritdoc />
    public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default) => _catalogStore.MutateAsync(mutation, cancellationToken);
}
