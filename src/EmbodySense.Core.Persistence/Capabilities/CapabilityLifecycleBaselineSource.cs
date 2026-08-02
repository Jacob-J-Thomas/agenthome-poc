using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Reads first-registration lifecycle baselines from the current proved catalog and activation stores.</summary>
public sealed class CapabilityLifecycleBaselineSource : ICapabilityLifecycleBaselineSource
{
    private const int PageSize = CapabilityCatalogLimits.MaximumPageSize;
    private readonly ICapabilityCatalogStore _catalogStore;
    private readonly ICapabilityArtifactStore _artifactStore;

    /// <summary>Creates a baseline adapter over existing proved domain stores.</summary>
    public CapabilityLifecycleBaselineSource(ICapabilityCatalogStore catalogStore, ICapabilityArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(catalogStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        _catalogStore = catalogStore;
        _artifactStore = artifactStore;
    }

    /// <inheritdoc />
    public async Task<CapabilityLifecycleBaseline?> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        string? cursor = null;
        CapabilityCatalogEntry? entry = null;
        long catalogRevision = 0;
        do
        {
            var read = await _catalogStore.ReadAsync(cursor, PageSize, cancellationToken);
            if (read.Status == CapabilityCatalogReadStatus.Unavailable || read.Page is null)
            {
                return null;
            }
            catalogRevision = read.Page.CatalogRevision;
            entry = read.Page.Entries.SingleOrDefault(candidate => candidate.Descriptor.Id.Equals(capabilityId));
            cursor = read.Page.NextCursor;
        }
        while (entry is null && cursor is not null);

        if (entry is null)
        {
            return null;
        }
        var activation = await _artifactStore.ReadAsync(capabilityId, cancellationToken);
        if (activation.Status != CapabilityArtifactStoreStatus.Applied || activation.Activation is null)
        {
            return null;
        }
        var state = new CapabilityLifecycleState(entry.Descriptor, activation.Activation.ArtifactDigest, entry.Lifecycle.Enablement == CapabilityEnablementState.Enabled, entry.Lifecycle.Retirement == CapabilityRetirementState.Removed, Math.Max(entry.Revision, activation.Activation.Revision), entry.LastOperationId, entry.UpdatedAtUtc);
        return new CapabilityLifecycleBaseline(state, catalogRevision, activation.Activation.Revision);
    }
}
