using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Deterministically resolves exact primary and eligible fallback profiles before the final admission receipt is committed.</summary>
public sealed class GovernedModelRoutingAdmissionService : IGovernedModelRoutingAdmissionService
{
    private const int MaximumCatalogPageSize = 50;
    private const int MaximumCatalogEntries = 512;
    private readonly ICapabilityCatalogStore _capabilityCatalog;
    private readonly IModelProfileMetadataSource _metadataSource;
    private readonly IModelProfileDefaultSource _defaultSource;
    private readonly IModelProfileAdapterRegistry _adapterRegistry;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a deterministic, side-effect-free routing resolver over current server-owned sources.</summary>
    public GovernedModelRoutingAdmissionService(
        ICapabilityCatalogStore capabilityCatalog,
        IModelProfileMetadataSource metadataSource,
        IModelProfileDefaultSource defaultSource,
        IModelProfileAdapterRegistry adapterRegistry,
        TimeProvider? timeProvider = null)
    {
        _capabilityCatalog = capabilityCatalog ?? throw new ArgumentNullException(nameof(capabilityCatalog));
        _metadataSource = metadataSource ?? throw new ArgumentNullException(nameof(metadataSource));
        _defaultSource = defaultSource ?? throw new ArgumentNullException(nameof(defaultSource));
        _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Resolves a complete routing snapshot which the governed admission service atomically embeds in its receipt.</summary>
    public async Task<GovernedModelRoutingAdmissionResult> AdmitAsync(GovernedModelRoutingAdmissionRequest? request, CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request))
        {
            return Result(GovernedModelRoutingAdmissionStatus.Invalid);
        }

        try
        {
            return await BuildSnapshotAsync(request!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedModelRoutingAdmissionStatus.Unavailable);
        }
    }

    private async Task<GovernedModelRoutingAdmissionResult> BuildSnapshotAsync(GovernedModelRoutingAdmissionRequest request, CancellationToken cancellationToken)
    {
        var seed = request.Seed;
        if (request.Nodes.Count == 0)
        {
            return Admitted(seed, Array.Empty<GovernedModelRoutingAdmissionEntry>(), null, null, null, null);
        }

        ModelProfileDefaultReadResult? currentDefault = null;
        if (request.Nodes.Any(node => node.Policy.Selector.Kind == GovernedModelSelectorKind.Inherit))
        {
            currentDefault = await _defaultSource.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!IsValidDefaultRead(currentDefault))
            {
                return Result(GovernedModelRoutingAdmissionStatus.Unavailable);
            }
            if (currentDefault!.Status != ModelProfileDefaultReadStatus.Found)
            {
                var node = request.Nodes.First(item => item.Policy.Selector.Kind == GovernedModelSelectorKind.Inherit);
                return currentDefault.Status == ModelProfileDefaultReadStatus.NotConfigured
                    ? Denied(seed, node, null, GovernedLoopAdmissionModelRoutingDenialReason.DefaultNotConfigured)
                    : Result(GovernedModelRoutingAdmissionStatus.Unavailable);
            }
        }

        var catalog = await ReadCapabilityCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (catalog is null)
        {
            return Result(GovernedModelRoutingAdmissionStatus.Unavailable);
        }

        Dictionary<string, CapabilityAdmissionPin> admittedPins;
        try
        {
            admittedPins = seed.CapabilityAdmission.Pins.ToDictionary(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal);
        }
        catch
        {
            return Result(GovernedModelRoutingAdmissionStatus.Invalid);
        }

        var entries = new List<GovernedModelRoutingAdmissionEntry>(request.Nodes.Count);
        string? adapterRegistryRevisionHash = null;
        foreach (var node in request.Nodes)
        {
            var candidateIds = node.Policy.ResolveCandidateOrder(currentDefault?.ProfileId);
            if (candidateIds.Count == 0)
            {
                return Denied(seed, node, null, GovernedLoopAdmissionModelRoutingDenialReason.DefaultNotConfigured);
            }

            var pins = new List<GovernedModelProfilePin>(candidateIds.Count);
            foreach (var candidateId in candidateIds)
            {
                if (!admittedPins.TryGetValue(candidateId.Value, out var capabilityPin))
                {
                    return Denied(seed, node, candidateId, GovernedLoopAdmissionModelRoutingDenialReason.CandidateNotAdmitted);
                }
                if (capabilityPin.Kind != CapabilityKind.ModelProfile)
                {
                    return Denied(seed, node, candidateId, GovernedLoopAdmissionModelRoutingDenialReason.CandidateNotModelProfile);
                }
                if (!catalog.Entries.TryGetValue(candidateId.Value, out var lifecycleEntry)
                    || !PinMatchesCatalog(capabilityPin, lifecycleEntry)
                    || CurrentLifecycleReason(lifecycleEntry.Lifecycle) != ModelProfileAvailabilityReason.Ready)
                {
                    return Denied(seed, node, candidateId, GovernedLoopAdmissionModelRoutingDenialReason.CandidateLifecycleIneligible);
                }

                var metadata = await _metadataSource.ReadAsync(candidateId, cancellationToken).ConfigureAwait(false);
                if (!IsValidMetadataRead(metadata))
                {
                    return Result(GovernedModelRoutingAdmissionStatus.Unavailable);
                }
                if (metadata!.Status != ModelProfileSourceReadStatus.Found)
                {
                    return metadata.Status == ModelProfileSourceReadStatus.NotFound
                        ? Denied(seed, node, candidateId, GovernedLoopAdmissionModelRoutingDenialReason.CandidateMetadataIneligible)
                        : Result(GovernedModelRoutingAdmissionStatus.Unavailable);
                }

                var adapter = await _adapterRegistry.ReadPostureAsync(metadata.Metadata!, cancellationToken).ConfigureAwait(false);
                if (!ModelProfileCatalogService.IsAdapterPosture(adapter, metadata.Metadata!.ContentHash))
                {
                    return Result(GovernedModelRoutingAdmissionStatus.Unavailable);
                }

                if (!Equals(metadata.Metadata.DescriptorIdentity, capabilityPin.DescriptorIdentity))
                {
                    return Denied(seed, node, candidateId, GovernedLoopAdmissionModelRoutingDenialReason.CandidateMetadataIneligible);
                }
                if (adapter!.Status != ModelProfileAdapterPostureStatus.Ready)
                {
                    return adapter.Status == ModelProfileAdapterPostureStatus.Unavailable
                        ? Result(GovernedModelRoutingAdmissionStatus.Unavailable)
                        : Denied(seed, node, candidateId, GovernedLoopAdmissionModelRoutingDenialReason.CandidateAdapterIneligible);
                }
                if (adapterRegistryRevisionHash is not null
                    && !string.Equals(adapterRegistryRevisionHash, adapter.RegistryRevisionHash, StringComparison.Ordinal))
                {
                    return Result(GovernedModelRoutingAdmissionStatus.Unavailable);
                }
                adapterRegistryRevisionHash = adapter.RegistryRevisionHash;
                if (!node.Policy.Requirements.StaticallySatisfiedBy(metadata.Metadata, seed.Intent.Role.Identity.RoleId, node.NodeTypeId)
                    || node.AuthoredInputDataClasses is not null && !node.Policy.Requirements.SatisfiedBy(metadata.Metadata, node.AuthoredInputDataClasses, seed.Intent.Role.Identity.RoleId, node.NodeTypeId))
                {
                    return Denied(seed, node, candidateId, GovernedLoopAdmissionModelRoutingDenialReason.CandidateRequirementsUnsatisfied);
                }

                pins.Add(GovernedModelProfilePin.Create(capabilityPin, metadata.Metadata, metadata.SourceRevisionHash!, adapter.RegistryRevisionHash));
            }

            entries.Add(GovernedModelRoutingAdmissionEntry.Create(
                1,
                node.NodeId,
                node.NodeTypeId,
                node.Policy.ContentHash,
                node.Policy.Requirements,
                node.AuthoredInputDataClasses is not null,
                node.AuthoredInputDataClasses ?? Array.Empty<CapabilityDataClass>(),
                pins[0],
                pins.Skip(1)));
        }

        return Admitted(seed, entries, catalog.CatalogRevision, currentDefault?.ProfileId, currentDefault?.SourceRevisionHash, adapterRegistryRevisionHash);
    }

    private static GovernedModelRoutingAdmissionResult Admitted(
        GovernedModelRoutingAdmissionSeed seed,
        IReadOnlyList<GovernedModelRoutingAdmissionEntry> entries,
        long? capabilityCatalogRevision,
        CapabilityId? resolvedDefaultProfileId,
        string? defaultSourceRevisionHash,
        string? adapterRegistryRevisionHash)
    {
        var intent = seed.Intent;
        var binding = seed.Binding;
        var role = intent.Role;
        var snapshot = GovernedModelRoutingAdmissionSnapshot.Create(
            1,
            intent.WorkspaceId,
            intent.OperationId,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            GovernedLoopAdmissionContractHash.ComputeExecutionBindingReferenceHash(binding),
            binding.RunId,
            binding.Revision.GraphId,
            binding.Revision.RevisionId,
            binding.Revision.ExecutableHash,
            binding.ExecutionGeneration,
            role.Identity.RoleId,
            role.Identity.Revision,
            role.ContentHash,
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(seed.CapabilityAdmission),
            GovernedLoopAdmissionContractHash.ComputeAdmissionAuthorityReferenceHash(seed.GrantProfile, seed.GrantBoundary, seed.GrantDependencyEvidenceHash, seed.EffectiveAuthority),
            capabilityCatalogRevision,
            resolvedDefaultProfileId,
            defaultSourceRevisionHash,
            adapterRegistryRevisionHash,
            seed.EvaluatedAtUtc,
            entries);
        return new GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus.Admitted, snapshot);
    }

    private async Task<CapabilityCatalogSnapshot?> ReadCapabilityCatalogAsync(CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, CapabilityCatalogEntry>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        long? revision = null;
        do
        {
            CapabilityCatalogReadResult read;
            try
            {
                read = await _capabilityCatalog.ReadAsync(cursor, MaximumCatalogPageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }

            if (read is null || read.Status != CapabilityCatalogReadStatus.Available || read.Page is null || read.Page.CatalogRevision < 0 || revision is not null && revision != read.Page.CatalogRevision)
            {
                return null;
            }

            IReadOnlyList<CapabilityCatalogEntry> page;
            try
            {
                page = ModelProfileApplicationContractCopy.Snapshot(read.Page.Entries, MaximumCatalogPageSize, nameof(read.Page.Entries));
            }
            catch
            {
                return null;
            }

            revision = read.Page.CatalogRevision;
            var prior = cursor;
            foreach (var entry in page)
            {
                if (!ModelProfileCatalogService.IsValidCatalogEntry(entry)
                    || prior is not null && string.Compare(entry.Descriptor.Id.Value, prior, StringComparison.Ordinal) <= 0
                    || entries.Count == MaximumCatalogEntries
                    || !entries.TryAdd(entry.Descriptor.Id.Value, entry))
                {
                    return null;
                }
                prior = entry.Descriptor.Id.Value;
            }

            var next = read.Page.NextCursor;
            if (next is not null && (page.Count == 0 || !CapabilityId.TryParse(next, out _, out _) || !string.Equals(next, prior, StringComparison.Ordinal) || !seenCursors.Add(next)))
            {
                return null;
            }
            cursor = next;
        }
        while (cursor is not null);

        return revision is null ? null : new CapabilityCatalogSnapshot(entries, revision.Value);
    }

    private static bool IsValidRequest(GovernedModelRoutingAdmissionRequest? request)
    {
        try
        {
            if (request is null || request.Seed is null || request.Nodes is null)
            {
                return false;
            }
            var seed = request.Seed;
            var nodes = request.Nodes;
            if (!GovernedLoopAdmissionValidator.Validate(seed.Intent).IsValid
                || !GovernedLoopExecutionValidator.Validate(seed.Binding).IsValid
                || !AuthorityProfileValidator.ValidateCeiling(seed.EffectiveAuthority).IsValid
                || CapabilityAdmissionSnapshotValidator.Validate(seed.CapabilityAdmission) is not null
                || seed.EvaluatedAtUtc == default
                || seed.EvaluatedAtUtc.Offset != TimeSpan.Zero
                || seed.CapabilityAdmission.AdmittedAtUtc > seed.EvaluatedAtUtc
                || !ModelProfileCatalogService.IsHash(seed.GrantDependencyEvidenceHash)
                || !string.Equals(seed.Intent.WorkspaceId, seed.CapabilityAdmission.WorkspaceScopeId, StringComparison.Ordinal)
                || !Equals(seed.Binding.Revision, seed.Intent.Publication.Revision)
                || nodes.Count > GovernedModelContractLimits.MaxAdmissionEntries
                || nodes.Any(node => node is null || !IsIdentifier(node.NodeId) || !IsIdentifier(node.NodeTypeId) || !GovernedModelContractValidator.IsValid(node.Policy) || !IsCanonicalDataClasses(node.AuthoredInputDataClasses))
                || nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != nodes.Count
                || !nodes.SequenceEqual(nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal)))
            {
                return false;
            }

            _ = GovernedLoopAdmissionContractHash.ComputeAdmissionAuthorityReferenceHash(seed.GrantProfile, seed.GrantBoundary, seed.GrantDependencyEvidenceHash, seed.EffectiveAuthority);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCanonicalDataClasses(IReadOnlyList<CapabilityDataClass>? values)
    {
        if (values is null)
        {
            return true;
        }
        try
        {
            return values.Count <= CapabilityContractLimits.MaxDataClasses
                && values.All(value => CapabilityDataClass.TryParse(value.Value, out var parsed, out _) && value.Equals(parsed))
                && values.Select(value => value.Value).SequenceEqual(values.Select(value => value.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                && values.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() == values.Count;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidDefaultRead(ModelProfileDefaultReadResult? read)
        => read is not null && Enum.IsDefined(read.Status) && read.Status != 0
            && (read.Status == ModelProfileDefaultReadStatus.Found && read.ProfileId is not null && CapabilityId.TryParse(read.ProfileId.Value, out var parsed, out _) && read.ProfileId.Equals(parsed) && ModelProfileCatalogService.IsHash(read.SourceRevisionHash)
                || read.Status is ModelProfileDefaultReadStatus.NotConfigured or ModelProfileDefaultReadStatus.Unavailable && read.ProfileId is null && read.SourceRevisionHash is null);

    private static bool IsValidMetadataRead(ModelProfileSourceReadResult? read)
        => read is not null && Enum.IsDefined(read.Status) && read.Status != 0
            && (read.Status == ModelProfileSourceReadStatus.Found && GovernedModelContractValidator.IsValid(read.Metadata) && ModelProfileCatalogService.IsHash(read.SourceRevisionHash)
                || read.Status is ModelProfileSourceReadStatus.NotFound or ModelProfileSourceReadStatus.Unavailable && read.Metadata is null && read.SourceRevisionHash is null);

    private static bool PinMatchesCatalog(CapabilityAdmissionPin pin, CapabilityCatalogEntry entry)
        => CapabilityAdmissionPinValidator.IsValid(pin)
            && Equals(pin.DescriptorIdentity, entry.Lifecycle.DescriptorIdentity)
            && pin.Kind == entry.Descriptor.Kind
            && Equals(pin.Implementation, entry.Descriptor.Implementation)
            && Equals(pin.Provenance, entry.Descriptor.Provenance)
            && string.Equals(pin.SafeDescription, entry.Descriptor.Purpose, StringComparison.Ordinal);

    private static ModelProfileAvailabilityReason CurrentLifecycleReason(CapabilityLifecycleSnapshot lifecycle)
    {
        if (lifecycle.Declaration != CapabilityDeclarationState.Declared || lifecycle.Installation != CapabilityInstallationState.Installed || lifecycle.Enablement != CapabilityEnablementState.Enabled)
        {
            return ModelProfileAvailabilityReason.LifecycleUnavailable;
        }
        return lifecycle.Trust == CapabilityTrustState.Verified && lifecycle.Health == CapabilityHealthState.Healthy && lifecycle.Retirement == CapabilityRetirementState.Active
            ? ModelProfileAvailabilityReason.Ready
            : ModelProfileAvailabilityReason.EvidenceUnavailable;
    }

    private static bool IsIdentifier(string? value)
        => value is { Length: >= 1 and <= GovernedModelContractLimits.MaxIdentifierCharacters }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static GovernedModelRoutingAdmissionResult Result(GovernedModelRoutingAdmissionStatus status) => new(status, null);

    private static GovernedModelRoutingAdmissionResult Denied(
        GovernedModelRoutingAdmissionSeed seed,
        GovernedModelRoutingNodeAdmissionRequest node,
        CapabilityId? candidateProfileId,
        GovernedLoopAdmissionModelRoutingDenialReason reason)
    {
        var proof = new GovernedLoopAdmissionModelRoutingDenialProof(
            GovernedLoopAdmissionModelRoutingDenialProof.CurrentSchemaVersion,
            node.NodeId,
            node.NodeTypeId,
            node.Policy.ContentHash,
            candidateProfileId,
            reason,
            GovernedLoopAdmissionContractHash.ComputeAuthorityCeilingReferenceHash(seed.EffectiveAuthority),
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(seed.CapabilityAdmission),
            seed.EvaluatedAtUtc);
        return new GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus.Ineligible, null, proof);
    }

    private sealed record CapabilityCatalogSnapshot(IReadOnlyDictionary<string, CapabilityCatalogEntry> Entries, long CatalogRevision);
}
