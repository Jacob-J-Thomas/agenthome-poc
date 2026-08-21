using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Joins safe model metadata to the shared capability lifecycle and exact adapter registry without granting authority.</summary>
public sealed class ModelProfileCatalogService
{
    private const int MaximumPageSize = 50;
    private const int MaximumCatalogEntries = 512;
    private readonly ICapabilityCatalogStore _capabilityCatalog;
    private readonly IModelProfileMetadataSource _metadataSource;
    private readonly IModelProfileAdapterRegistry _adapterRegistry;

    /// <summary>Creates the surface-neutral model-profile catalog.</summary>
    public ModelProfileCatalogService(ICapabilityCatalogStore capabilityCatalog, IModelProfileMetadataSource metadataSource, IModelProfileAdapterRegistry adapterRegistry)
    {
        _capabilityCatalog = capabilityCatalog ?? throw new ArgumentNullException(nameof(capabilityCatalog));
        _metadataSource = metadataSource ?? throw new ArgumentNullException(nameof(metadataSource));
        _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
    }

    /// <summary>Reads one bounded deterministic safe catalog page.</summary>
    public async Task<ModelProfileCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > MaximumPageSize || startAfterId is not null && !CapabilityId.TryParse(startAfterId, out _, out _))
        {
            return Result(ModelProfileCatalogReadStatus.Invalid);
        }

        try
        {
            var capabilitySnapshot = await ReadAllCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilitySnapshot is null)
            {
                return Result(ModelProfileCatalogReadStatus.Unavailable);
            }

            var filtered = capabilitySnapshot.Entries
                .Where(entry => entry.Descriptor.Kind == CapabilityKind.ModelProfile && (startAfterId is null || string.Compare(entry.Descriptor.Id.Value, startAfterId, StringComparison.Ordinal) > 0))
                .OrderBy(entry => entry.Descriptor.Id.Value, StringComparer.Ordinal)
                .ToArray();
            var projected = new List<ModelProfileCatalogItem>(Math.Min(maximumCount, filtered.Length));
            foreach (var entry in filtered.Take(maximumCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                projected.Add(await ProjectAsync(entry, capabilitySnapshot.CatalogRevision, cancellationToken).ConfigureAwait(false));
            }

            var next = filtered.Length > maximumCount ? projected[^1].ProfileId.Value : null;
            return new ModelProfileCatalogReadResult(ModelProfileCatalogReadStatus.Available, projected, next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(ModelProfileCatalogReadStatus.Unavailable);
        }
    }

    /// <summary>Reads one exact capability-backed model profile without trusting a caller-supplied page.</summary>
    public async Task<ModelProfileCatalogReadResult> ReadExactAsync(
        CapabilityId? profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId is null
            || !CapabilityId.TryParse(profileId.Value, out var parsed, out _)
            || !profileId.Equals(parsed))
        {
            return Result(ModelProfileCatalogReadStatus.Invalid);
        }

        try
        {
            var capabilitySnapshot = await ReadAllCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilitySnapshot is null)
            {
                return Result(ModelProfileCatalogReadStatus.Unavailable);
            }

            var exact = capabilitySnapshot.Entries
                .Where(entry => entry.Descriptor.Kind == CapabilityKind.ModelProfile
                    && entry.Descriptor.Id.Equals(profileId))
                .Take(2)
                .ToArray();
            if (exact.Length == 0)
            {
                return new ModelProfileCatalogReadResult(ModelProfileCatalogReadStatus.Available, [], null);
            }
            if (exact.Length != 1)
            {
                return Result(ModelProfileCatalogReadStatus.Unavailable);
            }

            var projected = await ProjectAsync(exact[0], capabilitySnapshot.CatalogRevision, cancellationToken).ConfigureAwait(false);
            return new ModelProfileCatalogReadResult(ModelProfileCatalogReadStatus.Available, [projected], null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(ModelProfileCatalogReadStatus.Unavailable);
        }
    }

    private async Task<CapabilityCatalogSnapshot?> ReadAllCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var results = new List<CapabilityCatalogEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        long? revision = null;
        do
        {
            var read = await _capabilityCatalog.ReadAsync(cursor, MaximumPageSize, cancellationToken).ConfigureAwait(false);
            if (read is null
                || !Enum.IsDefined(read.Status)
                || read.Status != CapabilityCatalogReadStatus.Available
                || read.Page is null
                || read.Page.CatalogRevision < 0)
            {
                return null;
            }

            IReadOnlyList<CapabilityCatalogEntry> pageEntries;
            try
            {
                pageEntries = ModelProfileApplicationContractCopy.Snapshot(read.Page.Entries, MaximumPageSize, nameof(read.Page.Entries));
            }
            catch
            {
                return null;
            }

            if (revision is not null && revision != read.Page.CatalogRevision)
            {
                return null;
            }

            revision = read.Page.CatalogRevision;
            var prior = cursor;
            foreach (var entry in pageEntries)
            {
                if (!IsValidCatalogEntry(entry)
                    || prior is not null && string.Compare(entry.Descriptor.Id.Value, prior, StringComparison.Ordinal) <= 0
                    || results.Count == MaximumCatalogEntries
                    || !seenIds.Add(entry.Descriptor.Id.Value))
                {
                    return null;
                }

                results.Add(entry);
                prior = entry.Descriptor.Id.Value;
            }

            if (read.Page.NextCursor is not null
                && (pageEntries.Count == 0
                    || !CapabilityId.TryParse(read.Page.NextCursor, out _, out _)
                    || !string.Equals(read.Page.NextCursor, prior, StringComparison.Ordinal)
                    || !seenCursors.Add(read.Page.NextCursor)))
            {
                return null;
            }

            cursor = read.Page.NextCursor;
        }
        while (cursor is not null);

        return revision is null ? null : new CapabilityCatalogSnapshot(Array.AsReadOnly(results.ToArray()), revision.Value);
    }

    private async Task<ModelProfileCatalogItem> ProjectAsync(CapabilityCatalogEntry entry, long catalogRevision, CancellationToken cancellationToken)
    {
        var metadataRead = await _metadataSource.ReadAsync(entry.Descriptor.Id, cancellationToken).ConfigureAwait(false);
        if (metadataRead is null
            || !Enum.IsDefined(metadataRead.Status)
            || metadataRead.Status != ModelProfileSourceReadStatus.Found
            || !GovernedModelContractValidator.IsValid(metadataRead.Metadata)
            || !IsHash(metadataRead.SourceRevisionHash)
            || !Equals(metadataRead.Metadata!.DescriptorIdentity, entry.Lifecycle.DescriptorIdentity))
        {
            return Placeholder(entry, catalogRevision, ModelProfileAvailabilityReason.MetadataUnavailable);
        }

        var reason = LifecycleReason(entry.Lifecycle);
        if (reason != ModelProfileAvailabilityReason.Ready)
        {
            return new ModelProfileCatalogItem(entry.Descriptor.Id, metadataRead.Metadata, reason, catalogRevision, null, metadataRead.SourceRevisionHash, CurrentPin(entry));
        }

        var adapter = await _adapterRegistry.ReadPostureAsync(metadataRead.Metadata, cancellationToken).ConfigureAwait(false);
        if (!IsAdapterPosture(adapter, metadataRead.Metadata.ContentHash))
        {
            return new ModelProfileCatalogItem(entry.Descriptor.Id, metadataRead.Metadata, ModelProfileAvailabilityReason.EvidenceUnavailable, catalogRevision, null, metadataRead.SourceRevisionHash, CurrentPin(entry));
        }

        return new ModelProfileCatalogItem(entry.Descriptor.Id, metadataRead.Metadata, adapter.Status == ModelProfileAdapterPostureStatus.Ready ? ModelProfileAvailabilityReason.Ready : ModelProfileAvailabilityReason.AdapterUnavailable, catalogRevision, adapter.RegistryRevisionHash, metadataRead.SourceRevisionHash, CurrentPin(entry));
    }

    private static ModelProfileCatalogItem Placeholder(CapabilityCatalogEntry entry, long catalogRevision, ModelProfileAvailabilityReason reason)
    {
        return new ModelProfileCatalogItem(entry.Descriptor.Id, null, reason, catalogRevision, null, null);
    }

    private static CapabilityAdmissionPin? CurrentPin(CapabilityCatalogEntry entry)
    {
        if (!CapabilityDescriptorIdentity.TryCreate(entry.Descriptor, out var identity, out _))
        {
            return null;
        }
        return new CapabilityAdmissionPin(
            identity!,
            entry.Descriptor.Kind,
            entry.Descriptor.Implementation,
            entry.Descriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            entry.Descriptor.Purpose);
    }

    private static ModelProfileAvailabilityReason LifecycleReason(CapabilityLifecycleSnapshot lifecycle)
    {
        if (lifecycle.Declaration != CapabilityDeclarationState.Declared || lifecycle.Installation != CapabilityInstallationState.Installed || lifecycle.Enablement != CapabilityEnablementState.Enabled)
        {
            return ModelProfileAvailabilityReason.LifecycleUnavailable;
        }

        if (lifecycle.Trust != CapabilityTrustState.Verified)
        {
            return ModelProfileAvailabilityReason.TrustUnavailable;
        }

        if (lifecycle.Health != CapabilityHealthState.Healthy)
        {
            return ModelProfileAvailabilityReason.HealthUnavailable;
        }

        return lifecycle.Retirement == CapabilityRetirementState.Active ? ModelProfileAvailabilityReason.Ready : ModelProfileAvailabilityReason.Retired;
    }

    internal static bool IsValidCatalogEntry(CapabilityCatalogEntry? entry)
    {
        return entry?.Descriptor is not null
            && entry.Lifecycle is not null
            && entry.Revision > 0
            && entry.UpdatedAtUtc != default
            && entry.UpdatedAtUtc.Offset == TimeSpan.Zero
            && !string.IsNullOrWhiteSpace(entry.LastOperationId)
            && CapabilityDescriptorValidator.Validate(entry.Descriptor).IsValid
            && CapabilityLifecycleSnapshotValidator.Validate(entry.Lifecycle).IsValid
            && CapabilityDescriptorIdentity.TryCreate(entry.Descriptor, out var identity, out _)
            && Equals(identity, entry.Lifecycle.DescriptorIdentity);
    }

    internal static bool IsAdapterPosture(ModelProfileAdapterPosture? posture, string metadataHash)
        => posture is not null
            && Enum.IsDefined(posture.Status)
            && posture.Status != 0
            && string.Equals(posture.ProfileMetadataHash, metadataHash, StringComparison.Ordinal)
            && IsHash(posture.RegistryRevisionHash);

    internal static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ModelProfileCatalogReadResult Result(ModelProfileCatalogReadStatus status) => new(status, Array.Empty<ModelProfileCatalogItem>(), null);

    private sealed record CapabilityCatalogSnapshot(IReadOnlyList<CapabilityCatalogEntry> Entries, long CatalogRevision);
}
