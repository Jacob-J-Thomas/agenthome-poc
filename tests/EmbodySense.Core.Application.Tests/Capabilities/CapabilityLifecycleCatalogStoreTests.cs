using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityLifecycleCatalogStoreTests
{
    [Fact]
    public async Task Current_lifecycle_state_projects_descriptor_enablement_and_tombstone_without_granting_other_axes()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(manifest.Descriptor, out var identity, out _));
        var catalogLifecycle = new CapabilityLifecycleSnapshot(1, identity!, CapabilityDeclarationState.Declared, CapabilityInstallationState.Installed, CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Verified);
        var catalogEntry = new CapabilityCatalogEntry(manifest.Descriptor, catalogLifecycle, 5, DateTimeOffset.Parse("2026-08-01T12:00:00Z"), "catalog-enable");
        var catalog = new RecordingCapabilityCatalogStore { ReadResult = new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(7, [catalogEntry], null), "available") };
        var lifecycle = new StubCapabilityLifecycleMutationStore();
        var replacement = manifest.Descriptor with { Version = CapabilityArtifactTestData.Version("2.0.0") };
        lifecycle.ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, new CapabilityLifecycleState(replacement, manifest.Checksum, false, true, 9, "remove-v2", DateTimeOffset.Parse("2026-08-01T13:00:00Z")), [], [], 9, "current");

        var read = await new CapabilityLifecycleCatalogStore(catalog, lifecycle).ReadAsync(null, 100);
        var projected = Assert.Single(read.Page!.Entries);

        Assert.Equal(CapabilityCatalogReadStatus.Available, read.Status);
        Assert.Equal("2.0.0", projected.Descriptor.Version.Value);
        Assert.Equal(CapabilityEnablementState.Disabled, projected.Lifecycle.Enablement);
        Assert.Equal(CapabilityRetirementState.Removed, projected.Lifecycle.Retirement);
        Assert.Equal(CapabilityInstallationState.Installed, projected.Lifecycle.Installation);
        Assert.Equal(CapabilityHealthState.Healthy, projected.Lifecycle.Health);
        Assert.Equal(CapabilityTrustState.Verified, projected.Lifecycle.Trust);
    }

    [Fact]
    public async Task Recovered_or_incomplete_lifecycle_state_makes_admission_catalog_unavailable()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(manifest.Descriptor, out var identity, out _));
        var entry = new CapabilityCatalogEntry(manifest.Descriptor, new CapabilityLifecycleSnapshot(1, identity!, CapabilityDeclarationState.Declared, CapabilityInstallationState.Installed, CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Verified), 5, DateTimeOffset.Parse("2026-08-01T12:00:00Z"), "enabled");
        var catalog = new RecordingCapabilityCatalogStore { ReadResult = new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(7, [entry], null), "available") };
        var lifecycle = new StubCapabilityLifecycleMutationStore { ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.RecoveredLastProved, null, [], [], 0, "recovered") };
        var projection = new CapabilityLifecycleCatalogStore(catalog, lifecycle);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await projection.ReadAsync(null, 100)).Status);
        lifecycle.ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, null, [], [], 1, "incomplete");
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await projection.ReadAsync(null, 100)).Status);
    }

    [Fact]
    public async Task Unregistered_capabilities_retain_catalog_state_and_catalog_mutations_delegate()
    {
        var manifest = CapabilityArtifactTestData.Manifest();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(manifest.Descriptor, out var identity, out _));
        var entry = new CapabilityCatalogEntry(manifest.Descriptor, new CapabilityLifecycleSnapshot(1, identity!, CapabilityDeclarationState.Declared, CapabilityInstallationState.Installed, CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Verified), 5, DateTimeOffset.Parse("2026-08-01T12:00:00Z"), "enabled");
        var catalog = new RecordingCapabilityCatalogStore { ReadResult = new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(7, [entry], null), "available") };
        var lifecycle = new StubCapabilityLifecycleMutationStore();
        var projection = new CapabilityLifecycleCatalogStore(catalog, lifecycle);
        var mutation = new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Disable, "delegated-mutation", 7, manifest.Descriptor.Id, null);

        Assert.Same(entry, Assert.Single((await projection.ReadAsync(null, 100)).Page!.Entries));
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, (await projection.MutateAsync(mutation)).Status);
        Assert.Same(mutation, Assert.Single(catalog.Mutations));
    }

    [Fact]
    public async Task Constructor_guards_ports_and_unavailable_catalog_is_preserved()
    {
        var catalog = new RecordingCapabilityCatalogStore { ReadResult = new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "unavailable") };
        var lifecycle = new StubCapabilityLifecycleMutationStore();
        var projection = new CapabilityLifecycleCatalogStore(catalog, lifecycle);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await projection.ReadAsync(null, 100)).Status);
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleCatalogStore(null!, lifecycle));
        Assert.Throws<ArgumentNullException>(() => new CapabilityLifecycleCatalogStore(catalog, null!));
    }
}
