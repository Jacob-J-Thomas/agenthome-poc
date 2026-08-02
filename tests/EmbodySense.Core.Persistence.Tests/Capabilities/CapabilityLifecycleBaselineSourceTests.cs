using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityLifecycleBaselineSourceTests
{
    [Fact]
    public async Task Proved_catalog_and_activation_are_mapped_without_granting_new_authority()
    {
        var descriptor = CapabilityLifecycleTestData.Descriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var lifecycle = new CapabilityLifecycleSnapshot(1, identity!, CapabilityDeclarationState.Declared, CapabilityInstallationState.Installed, CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Verified);
        var entry = new CapabilityCatalogEntry(descriptor, lifecycle, 5, DateTimeOffset.Parse("2026-08-01T12:00:00Z"), "catalog-enable");
        var catalog = new StubLifecycleCatalogStore { ReadResult = new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(9, [entry], null), "available") };
        var activation = new CapabilityArtifactActivation(descriptor.Id, CapabilityLifecycleTestData.Digest("active"), null, 7, entry.UpdatedAtUtc);
        var artifacts = new StubLifecycleArtifactStore { ReadResult = new CapabilityArtifactStoreResult(CapabilityArtifactStoreStatus.Applied, activation, "available") };

        var baseline = await new CapabilityLifecycleBaselineSource(catalog, artifacts, new StubCapabilityAuthorityTransaction()).ReadAsync(descriptor.Id);

        Assert.NotNull(baseline);
        Assert.Equal(9, baseline.CatalogRevision);
        Assert.Equal(7, baseline.ActivationRevision);
        Assert.True(baseline.State.IsEnabled);
        Assert.False(baseline.State.IsRemoved);
        Assert.Equal(activation.ArtifactDigest, baseline.State.ArtifactDigest);
        Assert.Equal(CapabilityCatalogLimits.MaximumPageSize, catalog.LastMaximumCount);
    }

    [Fact]
    public async Task Unknown_unproved_or_unactivated_capability_has_no_registration_baseline()
    {
        var descriptor = CapabilityLifecycleTestData.Descriptor();
        var authority = new StubCapabilityAuthorityTransaction();
        var unavailable = new CapabilityLifecycleBaselineSource(new StubLifecycleCatalogStore(), new StubLifecycleArtifactStore(), authority);
        Assert.Null(await unavailable.ReadAsync(descriptor.Id));
        var emptyCatalog = new StubLifecycleCatalogStore { ReadResult = new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(0, [], null), "empty") };
        Assert.Null(await new CapabilityLifecycleBaselineSource(emptyCatalog, new StubLifecycleArtifactStore(), authority).ReadAsync(descriptor.Id));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var lifecycle = new CapabilityLifecycleSnapshot(1, identity!, CapabilityDeclarationState.Declared, CapabilityInstallationState.Installed, CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Verified);
        var entry = new CapabilityCatalogEntry(descriptor, lifecycle, 1, DateTimeOffset.UtcNow, "catalog");
        var catalog = new StubLifecycleCatalogStore { ReadResult = new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(1, [entry], null), "available") };
        Assert.Null(await new CapabilityLifecycleBaselineSource(catalog, new StubLifecycleArtifactStore(), authority).ReadAsync(descriptor.Id));
    }
}
