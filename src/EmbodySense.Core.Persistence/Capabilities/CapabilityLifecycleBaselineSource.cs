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
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates a baseline adapter over existing proved domain stores.</summary>
    public CapabilityLifecycleBaselineSource(ICapabilityCatalogStore catalogStore, ICapabilityArtifactStore artifactStore, ICapabilityAuthorityTransaction authorityTransaction)
    {
        ArgumentNullException.ThrowIfNull(catalogStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(authorityTransaction);
        _catalogStore = catalogStore;
        _artifactStore = artifactStore;
        _authorityTransaction = authorityTransaction;
    }

    /// <inheritdoc />
    public Task<CapabilityLifecycleBaseline?> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => ReadCoreAsync(capabilityId, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityLifecycleBaseline?> ReadCoreAsync(CapabilityId capabilityId, CancellationToken cancellationToken)
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
