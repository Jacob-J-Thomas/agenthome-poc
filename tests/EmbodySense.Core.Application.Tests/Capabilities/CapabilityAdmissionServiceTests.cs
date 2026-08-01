using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityAdmissionServiceTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-01T12:00:00+00:00");

    [Fact]
    public async Task Admission_persists_exact_descriptor_implementation_provenance_and_resolution_evidence()
    {
        var entry = Entry();
        var requirements = Manifest();
        var service = Service(new MutableCatalogStore([entry]));

        var result = await service.AdmitAsync(requirements, [entry.Descriptor.Id]);

        Assert.True(result.IsAdmitted, result.Detail);
        var snapshot = Assert.IsType<CapabilityAdmissionSnapshot>(result.Snapshot);
        Assert.True(CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _));
        Assert.Equal("workspace-test", snapshot.WorkspaceScopeId);
        Assert.Equal(_now, snapshot.AdmittedAtUtc);
        Assert.Equal(requirementsHash!.Value, snapshot.RequirementsHash);
        var pin = Assert.Single(snapshot.Pins);
        Assert.Equal(entry.Lifecycle.DescriptorIdentity, pin.DescriptorIdentity);
        Assert.Equal(entry.Descriptor.Kind, pin.Kind);
        Assert.Equal(entry.Descriptor.Implementation, pin.Implementation);
        Assert.Equal(entry.Descriptor.Provenance, pin.Provenance);
        Assert.Equal(entry.Descriptor.Purpose, pin.SafeDescription);
        Assert.Null(pin.Artifact.Checksum);
        Assert.Null(pin.Artifact.Signature);
        var evidence = Assert.Single(snapshot.Evidence);
        Assert.Equal("Selected", evidence.Outcome);
        Assert.Equal(pin.DescriptorIdentity, evidence.SelectedIdentity);
        Assert.Null(CapabilityAdmissionSnapshotValidator.Validate(snapshot));
    }

    [Theory]
    [InlineData(CapabilityEnablementState.Disabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Verified)]
    [InlineData(CapabilityEnablementState.Enabled, CapabilityHealthState.Degraded, CapabilityRetirementState.Active, CapabilityTrustState.Verified)]
    [InlineData(CapabilityEnablementState.Enabled, CapabilityHealthState.Unavailable, CapabilityRetirementState.Active, CapabilityTrustState.Verified)]
    [InlineData(CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Removed, CapabilityTrustState.Verified)]
    [InlineData(CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Rejected)]
    public async Task Admission_rejects_capabilities_that_are_not_currently_effect_ready(
        CapabilityEnablementState enablement,
        CapabilityHealthState health,
        CapabilityRetirementState retirement,
        CapabilityTrustState trust)
    {
        var entry = Entry(enablement, health, retirement, trust);

        var result = await Service(new MutableCatalogStore([entry])).AdmitAsync(Manifest(), [entry.Descriptor.Id]);

        Assert.False(result.IsAdmitted);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Admission_rejects_missing_requirements_narrower_authority_and_noncurrent_catalog_state()
    {
        var entry = Entry();
        var requirements = Manifest();

        var missing = await Service(new MutableCatalogStore([])).AdmitAsync(requirements, [entry.Descriptor.Id]);
        var narrower = await Service(new MutableCatalogStore([entry])).AdmitAsync(requirements, []);
        var recovered = await Service(new MutableCatalogStore([entry]) { Status = CapabilityCatalogReadStatus.RecoveredLastProved }).AdmitAsync(requirements, [entry.Descriptor.Id]);

        Assert.False(missing.IsAdmitted);
        Assert.False(narrower.IsAdmitted);
        Assert.False(recovered.IsAdmitted);
    }

    [Fact]
    public async Task Revalidation_fails_closed_for_drift_removal_revocation_forgery_and_cross_workspace_without_mutating_history()
    {
        var original = Entry();
        var store = new MutableCatalogStore([original]);
        var service = Service(store);
        var admitted = await service.AdmitAsync(Manifest(), [original.Descriptor.Id]);
        var snapshot = Assert.IsType<CapabilityAdmissionSnapshot>(admitted.Snapshot);
        var persistedBefore = JsonSerializer.Serialize(snapshot);

        store.Entries = [Entry(purpose: "Drifted description")];
        Assert.False((await service.RevalidateAsync(snapshot, [original.Descriptor.Id])).IsValid);

        store.Entries = [Entry(retirement: CapabilityRetirementState.Removed)];
        Assert.False((await service.RevalidateAsync(snapshot, [original.Descriptor.Id])).IsValid);

        store.Entries = [Entry(trust: CapabilityTrustState.Rejected)];
        Assert.False((await service.RevalidateAsync(snapshot, [original.Descriptor.Id])).IsValid);

        store.Entries = [Entry(health: CapabilityHealthState.Degraded)];
        Assert.False((await service.RevalidateAsync(snapshot, [original.Descriptor.Id])).IsValid);

        store.Entries = [original];
        Assert.False((await service.RevalidateAsync(snapshot, [])).IsValid);
        Assert.False((await new CapabilityAdmissionService(store, "workspace-other").RevalidateAsync(snapshot, [original.Descriptor.Id])).IsValid);

        var malformedEvidence = snapshot with { Evidence = [null!] };
        Assert.False((await service.RevalidateAsync(malformedEvidence, [original.Descriptor.Id])).IsValid);

        var forged = snapshot with { RequirementsHash = new string('0', 64) };
        Assert.False((await service.RevalidateAsync(forged, [original.Descriptor.Id])).IsValid);
        Assert.Equal(persistedBefore, JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task Revalidation_rejects_noncanonical_or_oversized_resolution_evidence()
    {
        var entry = Entry();
        var service = Service(new MutableCatalogStore([entry]));
        var admitted = await service.AdmitAsync(Manifest(), [entry.Descriptor.Id]);
        var snapshot = Assert.IsType<CapabilityAdmissionSnapshot>(admitted.Snapshot);
        var evidence = Assert.Single(snapshot.Evidence);

        var unknownOutcome = snapshot with { Evidence = [evidence with { Outcome = "Invented" }] };
        var impossibleFailureOutcome = snapshot with { Evidence = [evidence with { Outcome = "Missing", SelectedIdentity = null }] };
        var oversizedDetail = snapshot with { Evidence = [evidence with { Detail = new string('x', CapabilityContractLimits.MaxCapabilityAdmissionEvidenceDetailCharacters + 1) }] };
        var unsafeDetail = snapshot with { Evidence = [evidence with { Detail = "Unsafe\u0001detail" }] };
        var oversizedEvidence = snapshot with { Evidence = Enumerable.Repeat(evidence, CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries + 1).ToArray() };
        var omitted = evidence with { Outcome = "OmittedOptional", IsOptional = true, SelectedIdentity = null };
        var duplicateOmittedEvidence = snapshot with { Evidence = [evidence, omitted, omitted] };
        Assert.True(CapabilityId.TryParse("org.example/unreachable", out var unreachableSubject, out _));
        var unreachableOmittedEvidence = snapshot with { Evidence = [evidence, omitted with { SubjectId = unreachableSubject! }] };

        Assert.False((await service.RevalidateAsync(unknownOutcome, [entry.Descriptor.Id])).IsValid);
        Assert.False((await service.RevalidateAsync(impossibleFailureOutcome, [entry.Descriptor.Id])).IsValid);
        Assert.False((await service.RevalidateAsync(oversizedDetail, [entry.Descriptor.Id])).IsValid);
        Assert.False((await service.RevalidateAsync(unsafeDetail, [entry.Descriptor.Id])).IsValid);
        Assert.False((await service.RevalidateAsync(oversizedEvidence, [entry.Descriptor.Id])).IsValid);
        Assert.False((await service.RevalidateAsync(duplicateOmittedEvidence, [entry.Descriptor.Id])).IsValid);
        Assert.False((await service.RevalidateAsync(unreachableOmittedEvidence, [entry.Descriptor.Id])).IsValid);
    }

    [Fact]
    public void Admission_snapshot_accepts_more_than_the_root_manifest_dependency_limit_when_the_selected_graph_is_coherent()
    {
        var snapshot = CreateCoherentAdmissionSnapshot(CapabilityContractLimits.MaxDependencyManifestDependencies + 1);

        Assert.Equal(CapabilityContractLimits.MaxCapabilityAdmissionPins, CapabilityDependencyResolutionLimits.Default.MaximumDependencies);
        Assert.Null(CapabilityAdmissionSnapshotValidator.Validate(snapshot));
    }

    private static CapabilityAdmissionService Service(MutableCatalogStore store) => new(store, "workspace-test", new FixedTimeProvider(_now));

    private static CapabilityCatalogEntry Entry(
        CapabilityEnablementState enablement = CapabilityEnablementState.Enabled,
        CapabilityHealthState health = CapabilityHealthState.Healthy,
        CapabilityRetirementState retirement = CapabilityRetirementState.Active,
        CapabilityTrustState trust = CapabilityTrustState.Verified,
        string purpose = "A safe test capability description.")
    {
        Assert.True(CapabilityId.TryParse("org.example/effect", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse("1.2.3", out var version, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var compatibility, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        var descriptor = new CapabilityDescriptor(
            CapabilityDescriptor.CurrentSchemaVersion,
            id!,
            CapabilityKind.Actuator,
            version!,
            new CapabilityImplementationIdentity(provider!, "effect-v1"),
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://example.test/effect", "revision-1", null),
            new CapabilityCompatibility(compatibility!, [CapabilityPlatform.Any]),
            purpose,
            schema!,
            schema!,
            new CapabilityResourceLimits(1_000, 1_024, 1_024, 1),
            CapabilitySideEffectClass.LocalReversible,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var lifecycle = new CapabilityLifecycleSnapshot(
            CapabilityLifecycleSnapshot.CurrentSchemaVersion,
            identity!,
            CapabilityDeclarationState.Declared,
            CapabilityInstallationState.Installed,
            enablement,
            health,
            retirement,
            trust);
        return new CapabilityCatalogEntry(descriptor, lifecycle, 7, _now, "test-operation");
    }

    private static CapabilityDependencyManifest Manifest()
    {
        Assert.True(CapabilityId.TryParse("org.example/loop", out var subject, out _));
        Assert.True(CapabilityId.TryParse("org.example/effect", out var dependency, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        return new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            [new CapabilityDependency(dependency!, range!)],
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
    }

    private static CapabilityAdmissionSnapshot CreateCoherentAdmissionSnapshot(int pinCount)
    {
        Assert.True(CapabilityId.TryParse("org.example/loop", out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var hash, out _));
        var capabilityIds = Enumerable.Range(0, pinCount).Select(index =>
        {
            Assert.True(CapabilityId.TryParse($"org.example/cap{index:D3}", out var id, out _));
            return id!;
        }).ToArray();
        var requirements = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            [new CapabilityDependency(capabilityIds[0], range!)],
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
        Assert.True(CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _));
        var pins = capabilityIds.Select(id => new CapabilityAdmissionPin(
            new CapabilityDescriptorIdentity(id, version!, hash!),
            CapabilityKind.GraphNode,
            new CapabilityImplementationIdentity(provider!, "test"),
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://example.test/capabilities", "1", null),
            new CapabilityDependencyArtifactMetadata(null, null),
            "A safe test capability description.")).ToArray();
        var evidence = capabilityIds.Select((id, index) => new CapabilityAdmissionEvidence(
            index == 0 ? subject! : capabilityIds[index - 1],
            id,
            range!,
            false,
            "Selected",
            pins[index].DescriptorIdentity,
            "A server-verified installed and available catalog candidate was selected.")).ToArray();
        return new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            "workspace-test",
            requirements,
            requirementsHash!.Value,
            pins,
            evidence,
            _now);
    }

    private sealed class MutableCatalogStore(IReadOnlyList<CapabilityCatalogEntry> entries) : ICapabilityCatalogStore
    {
        public IReadOnlyList<CapabilityCatalogEntry> Entries { get; set; } = entries;

        public CapabilityCatalogReadStatus Status { get; init; } = CapabilityCatalogReadStatus.Available;

        public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = Status == CapabilityCatalogReadStatus.Available ? new CapabilityCatalogPage(7, Entries, null) : null;
            return Task.FromResult(new CapabilityCatalogReadResult(Status, page, "Test catalog read."));
        }

        public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
