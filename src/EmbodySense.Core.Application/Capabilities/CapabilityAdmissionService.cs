using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Implements fail-closed loop capability admission over the governed catalog.</summary>
public sealed class CapabilityAdmissionService : ICapabilityAdmissionService
{
    private const int PageSize = 100;
    private readonly ICapabilityCatalogStore _catalogStore;
    private readonly CapabilityDependencyResolver _resolver;
    private readonly CapabilityVersion _hostContractVersion;
    private readonly CapabilityPlatform _hostPlatform;
    private readonly string _workspaceScopeId;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a workspace-bound admission service for one exact current host contract and platform.</summary>
    /// <param name="catalogStore">The governed catalog store.</param>
    /// <param name="workspaceScopeId">The exact workspace scope identity.</param>
    /// <param name="hostContractVersion">The current EmbodySense capability-host contract version.</param>
    /// <param name="hostPlatform">The current exact operating-system and process-architecture tuple.</param>
    /// <param name="authorityTransaction">The shared workspace authority fence spanning every catalog page and lifecycle overlay.</param>
    /// <param name="timeProvider">The optional trusted admission clock.</param>
    public CapabilityAdmissionService(ICapabilityCatalogStore catalogStore, string workspaceScopeId, CapabilityVersion hostContractVersion, CapabilityPlatform hostPlatform, ICapabilityAuthorityTransaction authorityTransaction, TimeProvider? timeProvider = null)
    {
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceScopeId);
        ArgumentNullException.ThrowIfNull(hostContractVersion);
        ArgumentNullException.ThrowIfNull(hostPlatform);
        ArgumentNullException.ThrowIfNull(authorityTransaction);
        if (hostPlatform.Equals(CapabilityPlatform.Any))
        {
            throw new ArgumentException("Capability admission requires one exact current host platform.", nameof(hostPlatform));
        }
        _workspaceScopeId = workspaceScopeId;
        _hostContractVersion = hostContractVersion;
        _hostPlatform = hostPlatform;
        _authorityTransaction = authorityTransaction;
        _resolver = new CapabilityDependencyResolver(hostContractVersion, hostPlatform);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<CapabilityAdmissionResult> AdmitAsync(CapabilityDependencyManifest requirements, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(allowedCapabilityIds);
        if (!CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _))
        {
            return Rejected("The loop capability requirement manifest is invalid.");
        }

        if (requirements.Required.Count == 0 && requirements.Optional.Count == 0)
        {
            var snapshot = new CapabilityAdmissionSnapshot(
                CapabilityAdmissionSnapshot.CurrentSchemaVersion,
                _workspaceScopeId,
                requirements,
                requirementsHash!.Value,
                [],
                [],
                _timeProvider.GetUtcNow().ToUniversalTime());
            var snapshotError = CapabilityAdmissionSnapshotValidator.Validate(snapshot);
            return snapshotError is null
                ? new CapabilityAdmissionResult(true, snapshot, "The loop requires no capabilities; an exact empty admission proof was recorded without consulting the catalog.")
                : Rejected($"Empty capability admission evidence is invalid: {snapshotError}");
        }

        return await _authorityTransaction.ExecuteAsync(transactionCancellationToken => AdmitUnderAuthorityAsync(requirements, requirementsHash!.Value, allowedCapabilityIds, transactionCancellationToken), cancellationToken);
    }

    private async Task<CapabilityAdmissionResult> AdmitUnderAuthorityAsync(CapabilityDependencyManifest requirements, string requirementsHash, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken)
    {
        var catalog = await ReadCurrentCatalogAsync(cancellationToken);
        if (catalog.Status != CapabilityCatalogSnapshotStatus.Available)
        {
            return Rejected(catalog.Detail);
        }

        var candidates = catalog.Entries.Select(entry => new CapabilityDependencyCatalogCandidate(entry, null, new CapabilityDependencyArtifactMetadata(null, null))).ToArray();
        var resolution = _resolver.Resolve(requirements, candidates);
        if (!resolution.IsResolved)
        {
            return Rejected("One or more required loop capabilities could not be resolved against the current proved catalog.");
        }

        if (!SelectedRootRequirementsAreAllowed(requirements, resolution.Evidence, allowedCapabilityIds))
        {
            return Rejected("Capability resolution exceeded the current loop or role authority ceiling.");
        }

        var pins = new List<CapabilityAdmissionPin>(resolution.Selected.Count);
        foreach (var selected in resolution.Selected)
        {
            var entry = catalog.Entries.SingleOrDefault(item => PinMatches(selected, item));
            if (entry is null || !IsCurrentlyAvailable(entry))
            {
                return Rejected("A resolved capability was not enabled, healthy, trusted, installed, host-compatible, and exact at admission.");
            }

            pins.Add(new CapabilityAdmissionPin(selected.DescriptorIdentity, entry.Descriptor.Kind, selected.Implementation, selected.Provenance, selected.Artifact, entry.Descriptor.Purpose));
        }

        var evidence = resolution.Evidence.Select(item => new CapabilityAdmissionEvidence(
            item.SubjectId,
            item.DependencyId,
            item.CompatibleVersionRange,
            item.IsOptional,
            item.Outcome.ToString(),
            item.Pin?.DescriptorIdentity,
            item.Detail)).ToArray();
        var snapshot = new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            _workspaceScopeId,
            requirements,
            requirementsHash,
            pins.OrderBy(item => item.DescriptorIdentity.Id.Value, StringComparer.Ordinal).ToArray(),
            evidence,
            _timeProvider.GetUtcNow().ToUniversalTime());
        var snapshotError = CapabilityAdmissionSnapshotValidator.Validate(snapshot);
        if (snapshotError is not null)
        {
            return Rejected($"Resolved capability admission evidence is invalid: {snapshotError}");
        }

        return new CapabilityAdmissionResult(true, snapshot, "Exact capability identities and resolution evidence were admitted without granting additional authority.");
    }

    /// <inheritdoc />
    public async Task<CapabilityRevalidationResult> RevalidateAsync(CapabilityAdmissionSnapshot snapshot, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(allowedCapabilityIds);
        var invalid = ValidateSnapshot(snapshot);
        if (invalid is not null)
        {
            return Invalid(CapabilityRevalidationStatus.InvalidSnapshot, invalid);
        }

        if (!string.Equals(snapshot.WorkspaceScopeId, _workspaceScopeId, StringComparison.Ordinal))
        {
            return Invalid(CapabilityRevalidationStatus.WorkspaceMismatch, "Capability admission evidence belongs to another workspace.");
        }

        if (!SelectedRootRequirementsAreAllowed(snapshot.Requirements, snapshot.Evidence, allowedCapabilityIds))
        {
            return Invalid(CapabilityRevalidationStatus.AuthorityNarrowed, "Current loop or role authority is narrower than the immutable admitted capability pins.");
        }

        return await _authorityTransaction.ExecuteAsync(transactionCancellationToken => RevalidateUnderAuthorityAsync(snapshot, allowedCapabilityIds, transactionCancellationToken), cancellationToken);
    }

    private async Task<CapabilityRevalidationResult> RevalidateUnderAuthorityAsync(CapabilityAdmissionSnapshot snapshot, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken)
    {
        var catalog = await ReadCurrentCatalogAsync(cancellationToken);
        if (catalog.Status != CapabilityCatalogSnapshotStatus.Available)
        {
            var status = catalog.Status == CapabilityCatalogSnapshotStatus.Unavailable
                ? CapabilityRevalidationStatus.CatalogUnavailable
                : CapabilityRevalidationStatus.CatalogAmbiguous;
            return Invalid(status, catalog.Detail);
        }

        var currentPins = new List<CapabilityAdmissionPin>(snapshot.Pins.Count);
        var observedPins = new List<CapabilityAdmissionPin>(snapshot.Pins.Count);
        var stoppedStatus = CapabilityRevalidationStatus.Active;
        string? stoppedDetail = null;
        foreach (var pin in snapshot.Pins)
        {
            var sameId = catalog.Entries.Where(item => item.Descriptor.Id.Equals(pin.DescriptorIdentity.Id)).ToArray();
            if (sameId.Length == 0)
            {
                SelectStoppedPosture(
                    CapabilityRevalidationStatus.PinMissing,
                    $"Admitted capability `{pin.DescriptorIdentity.Id.Value}` is absent from the current catalog.",
                    ref stoppedStatus,
                    ref stoppedDetail);
                continue;
            }

            var exact = sameId.Where(item => CapabilityDescriptorIdentity.TryCreate(item.Descriptor, out _, out _) && AdmissionPinMatches(pin, item)).ToArray();
            if (sameId.Length != 1 || exact.Length != 1)
            {
                var status = sameId.Length != 1 ? CapabilityRevalidationStatus.CatalogAmbiguous : CapabilityRevalidationStatus.PinDrifted;
                if (sameId.Length == 1 && IsCurrentlyAvailable(sameId[0]) && TryCreateCurrentPin(sameId[0], out var driftedPin))
                {
                    observedPins.Add(driftedPin!);
                }

                SelectStoppedPosture(
                    status,
                    $"Admitted capability `{pin.DescriptorIdentity.Id.Value}` did not resolve to one exact current descriptor, implementation, provenance, and safe description.",
                    ref stoppedStatus,
                    ref stoppedDetail);
                continue;
            }

            if (!IsCurrentlyAvailable(exact[0]))
            {
                SelectStoppedPosture(
                    CapabilityRevalidationStatus.PinInactive,
                    $"Admitted capability `{pin.DescriptorIdentity.Id.Value}` is disabled, unavailable, untrusted, uninstalled, host-incompatible, or removed.",
                    ref stoppedStatus,
                    ref stoppedDetail);
                continue;
            }

            currentPins.Add(pin);
        }

        if (stoppedStatus != CapabilityRevalidationStatus.Active)
        {
            return Invalid(stoppedStatus, stoppedDetail!, currentPins.AsReadOnly(), observedPins.AsReadOnly());
        }

        return new CapabilityRevalidationResult(
            true,
            currentPins.AsReadOnly(),
            "Every immutable admitted pin remains exact, currently available, and inside narrower authority.",
            CapabilityRevalidationStatus.Active,
            []);
    }

    private string? ValidateSnapshot(CapabilityAdmissionSnapshot snapshot)
    {
        var structural = CapabilityAdmissionSnapshotValidator.Validate(snapshot);
        if (structural is not null)
        {
            return structural;
        }

        return null;
    }

    private async Task<CatalogSnapshot> ReadCurrentCatalogAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CapabilityCatalogEntry>();
        string? cursor = null;
        long? revision = null;
        do
        {
            var read = await _catalogStore.ReadAsync(cursor, PageSize, cancellationToken);
            if (read.Status != CapabilityCatalogReadStatus.Available || read.Page is null)
            {
                return new CatalogSnapshot(CapabilityCatalogSnapshotStatus.Unavailable, [], "The current capability catalog is unavailable; last-proved or partial state cannot authorize a new effect.");
            }

            revision ??= read.Page.CatalogRevision;
            if (revision != read.Page.CatalogRevision || entries.Count + read.Page.Entries.Count > CapabilityDependencyResolutionLimits.Default.MaximumCandidates)
            {
                return new CatalogSnapshot(CapabilityCatalogSnapshotStatus.Ambiguous, [], "The capability catalog changed during its bounded read or exceeded the admission limit.");
            }

            entries.AddRange(read.Page.Entries);
            cursor = read.Page.NextCursor;
        }
        while (cursor is not null);

        return new CatalogSnapshot(CapabilityCatalogSnapshotStatus.Available, entries, "The current proved catalog was read completely.");
    }

    private bool IsCurrentlyAvailable(CapabilityCatalogEntry entry)
    {
        var lifecycle = entry.Lifecycle;
        return CapabilityDescriptorIdentity.TryCreate(entry.Descriptor, out var currentIdentity, out _)
            && lifecycle.DescriptorIdentity.Equals(currentIdentity)
            && lifecycle.Declaration == CapabilityDeclarationState.Declared
            && lifecycle.Installation == CapabilityInstallationState.Installed
            && lifecycle.Enablement == CapabilityEnablementState.Enabled
            && lifecycle.Health == CapabilityHealthState.Healthy
            && lifecycle.Retirement != CapabilityRetirementState.Removed
            && lifecycle.Trust == CapabilityTrustState.Verified
            && entry.Descriptor.Compatibility.HostVersionRange.Contains(_hostContractVersion)
            && entry.Descriptor.Compatibility.SupportedPlatforms.Any(platform => platform.Equals(CapabilityPlatform.Any) || platform.Equals(_hostPlatform));
    }

    private static bool PinMatches(CapabilityResolvedPin pin, CapabilityCatalogEntry entry)
    {
        return pin.DescriptorIdentity.Equals(entry.Lifecycle.DescriptorIdentity)
            && pin.Implementation.Equals(entry.Descriptor.Implementation)
            && pin.Provenance.Equals(entry.Descriptor.Provenance);
    }

    private static bool AdmissionPinMatches(CapabilityAdmissionPin pin, CapabilityCatalogEntry entry)
    {
        return pin.DescriptorIdentity.Equals(entry.Lifecycle.DescriptorIdentity)
            && pin.Kind == entry.Descriptor.Kind
            && pin.Implementation.Equals(entry.Descriptor.Implementation)
            && pin.Provenance.Equals(entry.Descriptor.Provenance)
            && string.Equals(pin.SafeDescription, entry.Descriptor.Purpose, StringComparison.Ordinal)
            && pin.Artifact.Checksum is null
            && pin.Artifact.Signature is null;
    }

    private static bool TryCreateCurrentPin(CapabilityCatalogEntry entry, out CapabilityAdmissionPin? pin)
    {
        pin = null;
        if (!CapabilityDescriptorIdentity.TryCreate(entry.Descriptor, out var identity, out _))
        {
            return false;
        }

        pin = new CapabilityAdmissionPin(
            identity!,
            entry.Descriptor.Kind,
            entry.Descriptor.Implementation,
            entry.Descriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            entry.Descriptor.Purpose);
        return true;
    }

    private static void SelectStoppedPosture(
        CapabilityRevalidationStatus candidate,
        string detail,
        ref CapabilityRevalidationStatus selected,
        ref string? selectedDetail)
    {
        if (RevalidationPriority(candidate) > RevalidationPriority(selected))
        {
            selected = candidate;
            selectedDetail = detail;
        }
    }

    private static int RevalidationPriority(CapabilityRevalidationStatus status) => status switch
    {
        CapabilityRevalidationStatus.CatalogAmbiguous => 4,
        CapabilityRevalidationStatus.PinDrifted => 3,
        CapabilityRevalidationStatus.PinInactive => 2,
        CapabilityRevalidationStatus.PinMissing => 1,
        _ => 0,
    };

    private static bool SelectedRootRequirementsAreAllowed(CapabilityDependencyManifest requirements, IReadOnlyList<CapabilityDependencyResolutionEvidence> evidence, IReadOnlyCollection<CapabilityId> allowedCapabilityIds)
    {
        var allowed = allowedCapabilityIds.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        return evidence.Where(item => item.SubjectId.Equals(requirements.SubjectId) && item.Outcome == CapabilityDependencyResolutionOutcome.Selected).All(item => allowed.Contains(item.DependencyId.Value));
    }

    private static bool SelectedRootRequirementsAreAllowed(CapabilityDependencyManifest requirements, IReadOnlyList<CapabilityAdmissionEvidence> evidence, IReadOnlyCollection<CapabilityId> allowedCapabilityIds)
    {
        var allowed = allowedCapabilityIds.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        return evidence.Where(item => item.SubjectId.Equals(requirements.SubjectId) && string.Equals(item.Outcome, CapabilityDependencyResolutionOutcome.Selected.ToString(), StringComparison.Ordinal)).All(item => allowed.Contains(item.DependencyId.Value));
    }

    private static CapabilityAdmissionResult Rejected(string detail) => new(false, null, detail);

    private static CapabilityRevalidationResult Invalid(
        CapabilityRevalidationStatus status,
        string detail,
        IReadOnlyList<CapabilityAdmissionPin>? effectivePins = null,
        IReadOnlyList<CapabilityAdmissionPin>? observedPins = null)
        => new(false, effectivePins ?? [], detail, status, observedPins ?? []);

    private sealed record CatalogSnapshot(CapabilityCatalogSnapshotStatus Status, IReadOnlyList<CapabilityCatalogEntry> Entries, string Detail);

}
