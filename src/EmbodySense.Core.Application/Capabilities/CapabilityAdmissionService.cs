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
    private readonly string _workspaceScopeId;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a workspace-bound admission service.</summary>
    public CapabilityAdmissionService(ICapabilityCatalogStore catalogStore, string workspaceScopeId, TimeProvider? timeProvider = null)
    {
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceScopeId);
        _workspaceScopeId = workspaceScopeId;
        _resolver = new CapabilityDependencyResolver();
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

        var catalog = await ReadCurrentCatalogAsync(cancellationToken);
        if (!catalog.IsAvailable)
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
                return Rejected("A resolved capability was not enabled, healthy, trusted, installed, and exact at admission.");
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
            requirementsHash!.Value,
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
            return Invalid(invalid);
        }

        if (!SelectedRootRequirementsAreAllowed(snapshot.Requirements, snapshot.Evidence, allowedCapabilityIds))
        {
            return Invalid("Current loop or role authority is narrower than the immutable admitted capability pins.");
        }

        var catalog = await ReadCurrentCatalogAsync(cancellationToken);
        if (!catalog.IsAvailable)
        {
            return Invalid(catalog.Detail);
        }

        foreach (var pin in snapshot.Pins)
        {
            var entry = catalog.Entries.SingleOrDefault(item => AdmissionPinMatches(pin, item));
            if (entry is null)
            {
                return Invalid($"Admitted capability `{pin.DescriptorIdentity.Id.Value}` is absent or its descriptor, implementation, or provenance drifted.");
            }

            if (!IsCurrentlyAvailable(entry))
            {
                return Invalid($"Admitted capability `{pin.DescriptorIdentity.Id.Value}` is disabled, unavailable, untrusted, uninstalled, or removed.");
            }
        }

        return new CapabilityRevalidationResult(true, snapshot.Pins.ToArray(), "Every immutable admitted pin remains exact, currently available, and inside narrower authority.");
    }

    private string? ValidateSnapshot(CapabilityAdmissionSnapshot snapshot)
    {
        var structural = CapabilityAdmissionSnapshotValidator.Validate(snapshot);
        if (structural is not null)
        {
            return structural;
        }

        if (!string.Equals(snapshot.WorkspaceScopeId, _workspaceScopeId, StringComparison.Ordinal))
        {
            return "Capability admission evidence belongs to another workspace.";
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
                return new CatalogSnapshot(false, [], "The current capability catalog is unavailable; last-proved or partial state cannot authorize a new effect.");
            }

            revision ??= read.Page.CatalogRevision;
            if (revision != read.Page.CatalogRevision || entries.Count + read.Page.Entries.Count > CapabilityDependencyResolutionLimits.Default.MaximumCandidates)
            {
                return new CatalogSnapshot(false, [], "The capability catalog changed during its bounded read or exceeded the admission limit.");
            }

            entries.AddRange(read.Page.Entries);
            cursor = read.Page.NextCursor;
        }
        while (cursor is not null);

        return new CatalogSnapshot(true, entries, "The current proved catalog was read completely.");
    }

    private static bool IsCurrentlyAvailable(CapabilityCatalogEntry entry)
    {
        var lifecycle = entry.Lifecycle;
        return lifecycle.Declaration == CapabilityDeclarationState.Declared
            && lifecycle.Installation == CapabilityInstallationState.Installed
            && lifecycle.Enablement == CapabilityEnablementState.Enabled
            && lifecycle.Health == CapabilityHealthState.Healthy
            && lifecycle.Retirement != CapabilityRetirementState.Removed
            && lifecycle.Trust == CapabilityTrustState.Verified;
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

    private static CapabilityRevalidationResult Invalid(string detail) => new(false, [], detail);

    private sealed record CatalogSnapshot(bool IsAvailable, IReadOnlyList<CapabilityCatalogEntry> Entries, string Detail);
}
