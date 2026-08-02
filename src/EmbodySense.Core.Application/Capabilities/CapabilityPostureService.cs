using System.Buffers;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Builds bounded redacted capability posture without granting authority or mutating governed state.</summary>
/// <remarks>
/// Administrative queries inspect only the bound workspace catalog, authenticated lifecycle evidence, and registered
/// dependent index. Model queries never read the ambient registry for projection; they revalidate immutable admission
/// evidence, then intersect exact admitted pins with explicit assignment and current narrower authority.
/// </remarks>
public sealed class CapabilityPostureService
{
    private const int MaximumCatalogPageSize = 50;
    // Schema-version-1 workspace catalogs retain at most 512 entries. Exact reads must cover the
    // complete governed catalog while still failing closed if a port exceeds that contract.
    private const int MaximumCatalogScanEntries = 512;
    private const int MaximumProjectedDependents = 100;
    private const int MaximumModelCapabilities = 16;
    private const int MaximumAuthorityIds = CapabilityContractLimits.MaxCapabilityAdmissionPins;
    private const int MaximumModelJsonBytes = 32_768;
    private static readonly CapabilityPostureError _invalidError = new("invalid_capability_posture_request", "The capability posture request is outside the bounded contract.");
    private static readonly CapabilityPostureError _unavailableError = new("capability_posture_unavailable", "Capability posture is unavailable for the requested scope.");
    private static readonly CapabilityPostureError _dependencyUnavailableError = new("capability_dependency_posture_unavailable", "Complete capability dependency posture is unavailable.");
    private static readonly CapabilityPostureError _limitError = new("capability_posture_limit_exceeded", "Capability posture exceeds the bounded projection contract.");
    private readonly ICapabilityCatalogStore _catalogStore;
    private readonly ICapabilityLifecycleMutationStore _lifecycleStore;
    private readonly ICapabilityDependentIndex _dependentIndex;
    private readonly ICapabilityAdmissionService _admissionService;
    private readonly CapabilityVersion _hostContractVersion;
    private readonly CapabilityPlatform _hostPlatform;

    /// <summary>Creates one read-only projection service over explicit workspace-bound ports.</summary>
    /// <param name="catalogStore">The governed catalog read port.</param>
    /// <param name="lifecycleStore">The authenticated lifecycle read port.</param>
    /// <param name="dependentIndex">The complete registered dependent index.</param>
    /// <param name="admissionService">The exact admission revalidation boundary.</param>
    /// <param name="hostContractVersion">The current host contract version.</param>
    /// <param name="hostPlatform">The current exact host platform.</param>
    public CapabilityPostureService(ICapabilityCatalogStore catalogStore, ICapabilityLifecycleMutationStore lifecycleStore, ICapabilityDependentIndex dependentIndex, ICapabilityAdmissionService admissionService, CapabilityVersion hostContractVersion, CapabilityPlatform hostPlatform)
    {
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        _lifecycleStore = lifecycleStore ?? throw new ArgumentNullException(nameof(lifecycleStore));
        _dependentIndex = dependentIndex ?? throw new ArgumentNullException(nameof(dependentIndex));
        _admissionService = admissionService ?? throw new ArgumentNullException(nameof(admissionService));
        _hostContractVersion = hostContractVersion ?? throw new ArgumentNullException(nameof(hostContractVersion));
        _hostPlatform = hostPlatform ?? throw new ArgumentNullException(nameof(hostPlatform));
        if (hostPlatform.Equals(CapabilityPlatform.Any))
        {
            throw new ArgumentException("Capability posture requires one exact current host platform.", nameof(hostPlatform));
        }
    }

    /// <summary>Reads one bounded deterministic administrative catalog page.</summary>
    /// <param name="startAfterId">The optional exclusive canonical identifier cursor.</param>
    /// <param name="maximumCount">The requested page size from one through fifty.</param>
    /// <param name="cancellationToken">The token used to cancel catalog, lifecycle, and dependent reads.</param>
    /// <returns>A safe page that confers no assignment or execution authority.</returns>
    public async Task<CapabilityPostureCatalogResult> ReadCatalogAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > MaximumCatalogPageSize || startAfterId is not null && !CapabilityId.TryParse(startAfterId, out _, out _))
        {
            return InvalidCatalog();
        }

        try
        {
            var catalog = await _catalogStore.ReadAsync(startAfterId, maximumCount, cancellationToken);
            if (catalog.Status == CapabilityCatalogReadStatus.Unavailable || catalog.Page is null)
            {
                return UnavailableCatalog();
            }

            var dependents = await _dependentIndex.CaptureAsync(cancellationToken);
            var projections = new List<CapabilityPostureProjection>(catalog.Page.Entries.Count);
            foreach (var entry in catalog.Page.Entries)
            {
                var lifecycle = await _lifecycleStore.ReadAsync(entry.Descriptor.Id, cancellationToken);
                var projection = Project(entry, lifecycle, dependents, catalog.Status == CapabilityCatalogReadStatus.RecoveredLastProved);
                if (projection is null)
                {
                    return UnavailableCatalog();
                }
                projections.Add(projection);
            }

            var recovered = catalog.Status == CapabilityCatalogReadStatus.RecoveredLastProved || projections.Any(item => item.IsRecovered);
            return new CapabilityPostureCatalogResult(recovered ? CapabilityPostureReadStatus.Recovered : CapabilityPostureReadStatus.Available, catalog.Page.CatalogRevision, projections, catalog.Page.NextCursor, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return UnavailableCatalog();
        }
    }

    /// <summary>Reads one exact administrative capability posture without scanning or exposing unrelated entries.</summary>
    /// <param name="capabilityId">The canonical capability identity.</param>
    /// <param name="cancellationToken">The token used to cancel catalog, lifecycle, and dependent reads.</param>
    /// <returns>The safe exact posture or a stable error.</returns>
    public async Task<CapabilityPostureQueryResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        if (capabilityId is null)
        {
            return new CapabilityPostureQueryResult(CapabilityPostureReadStatus.Invalid, null, _invalidError);
        }

        try
        {
            var catalog = await FindCatalogEntryAsync(capabilityId, cancellationToken);
            if (catalog.Status == CapabilityPostureReadStatus.NotFound)
            {
                return new CapabilityPostureQueryResult(CapabilityPostureReadStatus.NotFound, null, _unavailableError);
            }
            if (catalog.Entry is null)
            {
                return new CapabilityPostureQueryResult(CapabilityPostureReadStatus.Unavailable, null, _unavailableError);
            }

            var lifecycle = await _lifecycleStore.ReadAsync(capabilityId, cancellationToken);
            var dependents = await _dependentIndex.CaptureAsync(cancellationToken);
            var projection = Project(catalog.Entry, lifecycle, dependents, catalog.Status == CapabilityPostureReadStatus.Recovered);
            if (projection is null)
            {
                return new CapabilityPostureQueryResult(CapabilityPostureReadStatus.Unavailable, null, _unavailableError);
            }

            var status = projection.IsRecovered ? CapabilityPostureReadStatus.Recovered : CapabilityPostureReadStatus.Available;
            return new CapabilityPostureQueryResult(status, projection, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return new CapabilityPostureQueryResult(CapabilityPostureReadStatus.Unavailable, null, _unavailableError);
        }
    }

    /// <summary>Computes a deterministic dependent-impact projection without creating a lifecycle operation or mutation token.</summary>
    /// <param name="query">The exact read-only transition query.</param>
    /// <param name="cancellationToken">The token used to cancel catalog, lifecycle, and dependent reads.</param>
    /// <returns>The bounded impact posture or a stable error.</returns>
    public async Task<CapabilityPosturePreviewResult> PreviewAsync(CapabilityPosturePreviewQuery query, CancellationToken cancellationToken = default)
    {
        if (!IsValidPreviewQuery(query))
        {
            return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.Invalid, null, _invalidError);
        }

        try
        {
            var catalog = await FindCatalogEntryAsync(query.CapabilityId, cancellationToken);
            if (catalog.Status == CapabilityPostureReadStatus.NotFound)
            {
                return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.NotFound, null, _unavailableError);
            }
            if (catalog.Entry is null)
            {
                return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.Unavailable, null, _unavailableError);
            }

            var lifecycle = await _lifecycleStore.ReadAsync(query.CapabilityId, cancellationToken);
            var lifecycleRequiresState = lifecycle.Status is CapabilityLifecycleReadStatus.Available or CapabilityLifecycleReadStatus.RecoveredLastProved;
            if (lifecycle.Status == CapabilityLifecycleReadStatus.Unavailable || lifecycleRequiresState != (lifecycle.State is not null))
            {
                return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.Unavailable, null, _unavailableError);
            }
            var current = lifecycle.State?.Descriptor ?? catalog.Entry.Descriptor;
            if ((lifecycle.State?.IsRemoved == true || catalog.Entry.Lifecycle.Retirement == CapabilityRetirementState.Removed) && query.Operation != CapabilityLifecycleOperationKind.Rollback)
            {
                return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.Invalid, null, _invalidError);
            }

            var targetVersion = ResolveTargetVersion(query, lifecycle, current.Version);
            if (query.Operation is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback && targetVersion is null)
            {
                return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.NotFound, null, _unavailableError);
            }

            var dependents = await _dependentIndex.CaptureAsync(cancellationToken);
            if (dependents.Status != CapabilityDependentIndexStatus.Available)
            {
                return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.Unavailable, null, _dependencyUnavailableError);
            }

            var impacts = ProjectImpacts(query, targetVersion, IsTargetAdmitted(query, lifecycle), dependents.Dependents);
            var bounded = impacts.Take(MaximumProjectedDependents).ToArray();
            var preview = new CapabilityPosturePreviewProjection(
                query.CapabilityId.Value,
                query.Operation,
                current.Version.Value,
                targetVersion?.Value,
                dependents.Hash,
                impacts.Any(item => item.Outcome == CapabilityLifecycleImpactOutcome.Blocked),
                impacts.Any(item => item.Outcome == CapabilityLifecycleImpactOutcome.Degraded),
                bounded,
                impacts.Count > bounded.Length);
            var recovered = catalog.Status == CapabilityPostureReadStatus.Recovered || lifecycle.Status == CapabilityLifecycleReadStatus.RecoveredLastProved;
            return new CapabilityPosturePreviewResult(recovered ? CapabilityPostureReadStatus.Recovered : CapabilityPostureReadStatus.Available, preview, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return new CapabilityPosturePreviewResult(CapabilityPostureReadStatus.Unavailable, null, _unavailableError);
        }
    }

    /// <summary>Builds canonical model context from only exact admitted, explicitly assigned, and currently authorized pins.</summary>
    /// <param name="admission">The immutable admitted capability evidence.</param>
    /// <param name="assignedCapabilityIds">The exact admitted loop or node assignment visible for this inference.</param>
    /// <param name="currentAuthorityCapabilityIds">The current narrower authority ceiling.</param>
    /// <param name="cancellationToken">The token used to cancel revalidation.</param>
    /// <returns>Deterministic bounded JSON or a stable error with no ambient registry data.</returns>
    public async Task<CapabilityModelPostureResult> ReadModelContextAsync(CapabilityAdmissionSnapshot admission, IReadOnlyCollection<string> assignedCapabilityIds, IReadOnlyCollection<string> currentAuthorityCapabilityIds, CancellationToken cancellationToken = default)
    {
        if (admission is null || CapabilityAdmissionSnapshotValidator.Validate(admission) is not null || !TryParseExactIds(assignedCapabilityIds, out var assigned) || !TryParseExactIds(currentAuthorityCapabilityIds, out var current))
        {
            return InvalidModel();
        }

        try
        {
            var admittedIds = admission.Pins.Select(pin => pin.DescriptorIdentity.Id).ToArray();
            var revalidation = await _admissionService.RevalidateAsync(admission, admittedIds, cancellationToken);
            if (!PinsRemainExact(admission, revalidation))
            {
                return UnavailableModel();
            }

            var capabilities = revalidation.EffectivePins
                .Where(pin => assigned.ContainsKey(pin.DescriptorIdentity.Id.Value) && current.ContainsKey(pin.DescriptorIdentity.Id.Value))
                .OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
                .Select(pin => new CapabilityModelPostureProjection(pin.DescriptorIdentity.Id.Value, pin.DescriptorIdentity.Version.Value, Token(pin.Kind), pin.SafeDescription))
                .ToArray();
            if (capabilities.Length > MaximumModelCapabilities)
            {
                return LimitModel();
            }

            var canonicalJson = SerializeModelContext(capabilities);
            return Encoding.UTF8.GetByteCount(canonicalJson) <= MaximumModelJsonBytes
                ? new CapabilityModelPostureResult(CapabilityPostureReadStatus.Available, capabilities, canonicalJson, null)
                : LimitModel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return UnavailableModel();
        }
    }

    private async Task<CatalogEntryRead> FindCatalogEntryAsync(CapabilityId capabilityId, CancellationToken cancellationToken)
    {
        string? cursor = null;
        long? revision = null;
        var count = 0;
        var recovered = false;
        var observedCursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var read = await _catalogStore.ReadAsync(cursor, MaximumCatalogPageSize, cancellationToken);
            if (read.Status == CapabilityCatalogReadStatus.Unavailable || read.Page is null || revision is not null && revision != read.Page.CatalogRevision)
            {
                return new CatalogEntryRead(CapabilityPostureReadStatus.Unavailable, null);
            }
            revision ??= read.Page.CatalogRevision;
            recovered |= read.Status == CapabilityCatalogReadStatus.RecoveredLastProved;
            count += read.Page.Entries.Count;
            if (count > MaximumCatalogScanEntries)
            {
                return new CatalogEntryRead(CapabilityPostureReadStatus.Unavailable, null);
            }
            var entry = read.Page.Entries.SingleOrDefault(item => item.Descriptor.Id.Equals(capabilityId));
            if (entry is not null)
            {
                return new CatalogEntryRead(recovered ? CapabilityPostureReadStatus.Recovered : CapabilityPostureReadStatus.Available, entry);
            }
            cursor = read.Page.NextCursor;
            if (cursor is not null && !observedCursors.Add(cursor))
            {
                return new CatalogEntryRead(CapabilityPostureReadStatus.Unavailable, null);
            }
        }
        while (cursor is not null);

        return new CatalogEntryRead(recovered ? CapabilityPostureReadStatus.Unavailable : CapabilityPostureReadStatus.NotFound, null);
    }

    private CapabilityPostureProjection? Project(CapabilityCatalogEntry entry, CapabilityLifecycleReadResult lifecycle, CapabilityDependentIndexSnapshot dependents, bool catalogRecovered)
    {
        var hasLifecycleState = lifecycle?.Status is CapabilityLifecycleReadStatus.Available or CapabilityLifecycleReadStatus.RecoveredLastProved;
        if (entry?.Descriptor is null
            || entry.Lifecycle is null
            || lifecycle is null
            || lifecycle.Status == CapabilityLifecycleReadStatus.Unavailable
            || hasLifecycleState != (lifecycle.State is not null)
            || !CapabilityDescriptorIdentity.TryCreate(entry.Descriptor, out var catalogDescriptorIdentity, out _)
            || !CapabilityDescriptorIdentity.TryCreate(lifecycle.State?.Descriptor ?? entry.Descriptor, out var descriptorIdentity, out _)
            || !descriptorIdentity!.Id.Equals(entry.Descriptor.Id))
        {
            return null;
        }

        var descriptor = lifecycle.State?.Descriptor ?? entry.Descriptor;
        var lifecycleRemoved = lifecycle.State?.IsRemoved ?? false;
        var removed = lifecycleRemoved || entry.Lifecycle.Retirement == CapabilityRetirementState.Removed;
        var effectiveLifecycle = entry.Lifecycle with
        {
            DescriptorIdentity = descriptorIdentity!,
            Enablement = lifecycle.State is null
                ? entry.Lifecycle.Enablement
                : lifecycle.State.IsEnabled && !removed ? CapabilityEnablementState.Enabled : CapabilityEnablementState.Disabled,
            Retirement = removed ? CapabilityRetirementState.Removed : entry.Lifecycle.Retirement
        };
        var relevant = RelevantDependents(descriptor.Id, dependents.Dependents);
        var boundedDependents = relevant.Take(MaximumProjectedDependents).ToArray();
        var dependenciesAvailable = dependents.Status == CapabilityDependentIndexStatus.Available;
        var hostCompatible = IsHostCompatible(descriptor);
        var lifecycleEnabled = effectiveLifecycle.Enablement == CapabilityEnablementState.Enabled;
        var lifecycleDrift = !catalogDescriptorIdentity!.Equals(entry.Lifecycle.DescriptorIdentity);
        var requiredConflict = relevant.Any(item => item.RequirementKind == CapabilityRequirementKind.Required && !ParseRange(item.CompatibleVersionRange).Contains(descriptor.Version));
        var optionalConflict = relevant.Any(item => item.RequirementKind == CapabilityRequirementKind.Optional && !ParseRange(item.CompatibleVersionRange).Contains(descriptor.Version));
        var recovered = catalogRecovered || lifecycle.Status == CapabilityLifecycleReadStatus.RecoveredLastProved;
        var state = DetermineState(effectiveLifecycle, lifecycleEnabled, removed, hostCompatible, dependenciesAvailable, lifecycleDrift || requiredConflict, optionalConflict || lifecycle.Degradations.Count > 0, recovered);
        return new CapabilityPostureProjection(
            descriptor.Id.Value,
            descriptor.Version.Value,
            descriptorIdentity!.Hash.Value,
            Token(descriptor.Kind),
            descriptor.Purpose,
            descriptor.Implementation.ProviderId.Value,
            descriptor.Implementation.ImplementationId,
            Token(descriptor.Provenance.Kind),
            RedactSourceUri(descriptor.Provenance.SourceUri),
            descriptor.Provenance.SourceRevision,
            descriptor.Provenance.Integrity?.Value,
            descriptor.Compatibility.HostVersionRange.Value,
            descriptor.Compatibility.SupportedPlatforms.Select(item => item.ToString()).Order(StringComparer.Ordinal).ToArray(),
            hostCompatible,
            Token(descriptor.SideEffectClass),
            descriptor.Requirements.DataClasses.Select(item => item.Value).Order(StringComparer.Ordinal).ToArray(),
            Token(descriptor.Requirements.EgressMode),
            descriptor.Requirements.EgressDestinations.Order(StringComparer.Ordinal).ToArray(),
            descriptor.Requirements.Secrets.Select(item => item.Name).Order(StringComparer.Ordinal).ToArray(),
            state,
            Token(effectiveLifecycle.Declaration),
            Token(effectiveLifecycle.Installation),
            Token(effectiveLifecycle.Enablement),
            Token(effectiveLifecycle.Health),
            Token(effectiveLifecycle.Retirement),
            Token(effectiveLifecycle.Trust),
            lifecycleEnabled,
            removed,
            entry.Revision,
            lifecycle.State?.Revision,
            recovered,
            boundedDependents,
            dependenciesAvailable,
            relevant.Count > boundedDependents.Length);
    }

    private static CapabilityPostureState DetermineState(CapabilityLifecycleSnapshot lifecycle, bool lifecycleEnabled, bool removed, bool hostCompatible, bool dependenciesAvailable, bool requiredConflict, bool degraded, bool recovered)
    {
        if (removed)
        {
            return CapabilityPostureState.Removed;
        }
        if (lifecycle.Declaration != CapabilityDeclarationState.Declared || lifecycle.Installation != CapabilityInstallationState.Installed || lifecycle.Enablement != CapabilityEnablementState.Enabled || lifecycle.Health is CapabilityHealthState.Unknown or CapabilityHealthState.Unavailable || lifecycle.Trust != CapabilityTrustState.Verified || !lifecycleEnabled)
        {
            return CapabilityPostureState.Unavailable;
        }
        if (!hostCompatible)
        {
            return CapabilityPostureState.Incompatible;
        }
        if (!dependenciesAvailable || requiredConflict)
        {
            return CapabilityPostureState.DependencyConflict;
        }
        if (lifecycle.Health == CapabilityHealthState.Degraded || lifecycle.Retirement == CapabilityRetirementState.Deprecated || degraded || recovered)
        {
            return CapabilityPostureState.Degraded;
        }
        return CapabilityPostureState.Available;
    }

    private bool IsHostCompatible(CapabilityDescriptor descriptor)
    {
        return descriptor.Compatibility.HostVersionRange.Contains(_hostContractVersion)
            && descriptor.Compatibility.SupportedPlatforms.Any(platform => platform.Equals(CapabilityPlatform.Any) || platform.Equals(_hostPlatform));
    }

    private static IReadOnlyList<CapabilityPostureDependentProjection> RelevantDependents(CapabilityId capabilityId, IEnumerable<CapabilityDependent> dependents)
    {
        var projections = new List<CapabilityPostureDependentProjection>();
        foreach (var dependent in dependents ?? [])
        {
            Add(dependent.Manifest.Required, CapabilityRequirementKind.Required);
            Add(dependent.Manifest.Optional, CapabilityRequirementKind.Optional);
            void Add(IEnumerable<CapabilityDependency> requirements, CapabilityRequirementKind requirementKind)
            {
                projections.AddRange(requirements.Where(item => item.CapabilityId.Equals(capabilityId)).Select(item => new CapabilityPostureDependentProjection(dependent.Kind, dependent.Identity, dependent.Revision, requirementKind, item.CompatibleVersionRange.Value, dependent.AuthorityPosture)));
            }
        }
        return projections.OrderBy(item => item.Kind).ThenBy(item => item.Identity, StringComparer.Ordinal).ThenBy(item => item.RequirementKind).ToArray();
    }

    private static IReadOnlyList<CapabilityPosturePreviewImpact> ProjectImpacts(CapabilityPosturePreviewQuery query, CapabilityVersion? targetVersion, bool targetIsAdmitted, IEnumerable<CapabilityDependent> dependents)
    {
        var impacts = new List<CapabilityPosturePreviewImpact>();
        foreach (var dependent in RelevantDependents(query.CapabilityId, dependents))
        {
            var compatible = targetIsAdmitted && targetVersion is not null && ParseRange(dependent.CompatibleVersionRange).Contains(targetVersion);
            var outcome = compatible ? CapabilityLifecycleImpactOutcome.Preserved : dependent.RequirementKind == CapabilityRequirementKind.Required ? CapabilityLifecycleImpactOutcome.Blocked : CapabilityLifecycleImpactOutcome.Degraded;
            impacts.Add(new CapabilityPosturePreviewImpact(dependent, compatible, outcome));
        }
        return impacts;
    }

    private static CapabilityVersion? ResolveTargetVersion(CapabilityPosturePreviewQuery query, CapabilityLifecycleReadResult lifecycle, CapabilityVersion currentVersion)
    {
        return query.Operation switch
        {
            CapabilityLifecycleOperationKind.Enable => currentVersion,
            CapabilityLifecycleOperationKind.Upgrade => query.TargetVersion,
            CapabilityLifecycleOperationKind.Rollback => lifecycle.History.LastOrDefault()?.Descriptor.Version,
            _ => null
        };
    }

    private static bool IsTargetAdmitted(CapabilityPosturePreviewQuery query, CapabilityLifecycleReadResult lifecycle)
    {
        return query.Operation switch
        {
            CapabilityLifecycleOperationKind.Enable => true,
            CapabilityLifecycleOperationKind.Upgrade => true,
            CapabilityLifecycleOperationKind.Rollback => lifecycle.History.LastOrDefault() is { WasEnabled: true, WasRemoved: false },
            _ => false
        };
    }

    private static bool IsValidPreviewQuery(CapabilityPosturePreviewQuery? query)
    {
        return query?.CapabilityId is not null
            && Enum.IsDefined(query.Operation)
            && (query.Operation == CapabilityLifecycleOperationKind.Upgrade) == (query.TargetVersion is not null);
    }

    private static bool TryParseExactIds(IReadOnlyCollection<string>? values, out IReadOnlyDictionary<string, CapabilityId> parsed)
    {
        parsed = new Dictionary<string, CapabilityId>();
        if (values is null || values.Count > MaximumAuthorityIds)
        {
            return false;
        }
        var result = new Dictionary<string, CapabilityId>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!CapabilityId.TryParse(value, out var capabilityId, out _) || !result.TryAdd(value, capabilityId!))
            {
                return false;
            }
        }
        parsed = result;
        return true;
    }

    private static string SerializeModelContext(IReadOnlyList<CapabilityModelPostureProjection> capabilities)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WritePropertyName("capabilities");
            writer.WriteStartArray();
            foreach (var capability in capabilities)
            {
                writer.WriteStartObject();
                writer.WriteString("id", capability.Id);
                writer.WriteString("version", capability.Version);
                writer.WriteString("kind", capability.Kind);
                writer.WriteString("description", capability.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool PinsRemainExact(CapabilityAdmissionSnapshot admission, CapabilityRevalidationResult? revalidation)
    {
        return revalidation?.IsValid == true
            && revalidation.EffectivePins is not null
            && revalidation.EffectivePins.Count == admission.Pins.Count
            && revalidation.EffectivePins.All(pin => pin is not null && admission.Pins.Contains(pin))
            && revalidation.EffectivePins.Select(pin => pin.DescriptorIdentity.Id.Value).Distinct(StringComparer.Ordinal).Count() == revalidation.EffectivePins.Count;
    }

    private static string RedactSourceUri(string sourceUri)
    {
        return Uri.TryCreate(sourceUri, UriKind.Absolute, out var parsed) && parsed.IsFile ? "file:///redacted" : sourceUri;
    }

    private static CapabilityVersionRange ParseRange(string value)
    {
        return CapabilityVersionRange.TryParse(value, out var range, out _) ? range! : throw new FormatException("A projected dependent version range is invalid.");
    }

    private static string Token<T>(T value) where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());

    private static CapabilityPostureCatalogResult InvalidCatalog() => new(CapabilityPostureReadStatus.Invalid, null, [], null, _invalidError);

    private static CapabilityPostureCatalogResult UnavailableCatalog() => new(CapabilityPostureReadStatus.Unavailable, null, [], null, _unavailableError);

    private static CapabilityModelPostureResult InvalidModel() => new(CapabilityPostureReadStatus.Invalid, [], string.Empty, _invalidError);

    private static CapabilityModelPostureResult UnavailableModel() => new(CapabilityPostureReadStatus.Unavailable, [], string.Empty, _unavailableError);

    private static CapabilityModelPostureResult LimitModel() => new(CapabilityPostureReadStatus.Unavailable, [], string.Empty, _limitError);

    private static bool IsAvailabilityFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or OverflowException or JsonException;

    private sealed record CatalogEntryRead(CapabilityPostureReadStatus Status, CapabilityCatalogEntry? Entry);
}
