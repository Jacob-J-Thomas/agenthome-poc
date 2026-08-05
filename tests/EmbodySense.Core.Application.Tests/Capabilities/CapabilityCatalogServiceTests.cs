using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityCatalogServiceTests
{
    [Fact]
    public async Task Service_exposes_bounded_reads_and_every_explicit_lifecycle_transition()
    {
        var store = new RecordingCapabilityCatalogStore();
        var service = new CapabilityCatalogService(store);
        var descriptor = Descriptor();

        var read = await service.ReadAsync("org.example/a", 7);
        await service.DeclareAsync(descriptor, 0, "declare");
        await service.InstallAsync(descriptor.Id, 1, "install");
        await service.EnableAsync(descriptor.Id, 2, "enable");
        await service.DisableAsync(descriptor.Id, 3, "disable");
        await service.VerifyAsync(descriptor.Id, 4, "verify");
        await service.RejectTrustAsync(descriptor.Id, 5, "reject");
        await service.MarkHealthyAsync(descriptor.Id, 6, "healthy");
        await service.MarkDegradedAsync(descriptor.Id, 7, "degraded");
        await service.MarkUnavailableAsync(descriptor.Id, 8, "unavailable");
        await service.DeprecateAsync(descriptor.Id, 9, "deprecate");
        await service.RemoveAsync(descriptor.Id, 10, "remove");

        Assert.Equal("org.example/a:7", read.Detail);
        Assert.Equal(Enum.GetValues<CapabilityCatalogMutationKind>(), store.Mutations.Select(item => item.Kind));
        Assert.NotNull(store.Mutations[0].Descriptor);
        Assert.All(store.Mutations.Skip(1), item => Assert.Null(item.Descriptor));
        Assert.Equal(Enumerable.Range(0, 11).Select(value => (long)value), store.Mutations.Select(item => item.ExpectedCatalogRevision));
    }

    [Fact]
    public void Constructor_rejects_missing_store()
    {
        Assert.Throws<ArgumentNullException>(() => new CapabilityCatalogService(null!));
    }

    [Fact]
    public async Task Declaration_rejects_a_missing_descriptor_before_calling_the_store()
    {
        var store = new RecordingCapabilityCatalogStore();
        var service = new CapabilityCatalogService(store);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.DeclareAsync(null!, 0, "declare"));
        Assert.Empty(store.Mutations);
    }

    private static CapabilityDescriptor Descriptor()
    {
        _ = CapabilityId.TryParse("org.example/test", out var id, out _);
        _ = CapabilityProviderId.TryParse("org.example", out var provider, out _);
        _ = CapabilityVersion.TryParse("1.0.0", out var version, out _);
        _ = CapabilityVersionRange.TryParse("*", out var range, out _);
        _ = CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _);
        return new CapabilityDescriptor(1, id!, CapabilityKind.Skill, version!, new CapabilityImplementationIdentity(provider!, "test"), new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, "file:///test", null, null), new CapabilityCompatibility(range!, [CapabilityPlatform.Any]), "Test capability.", schema!, schema!, new CapabilityResourceLimits(1, 1, 1, 1), CapabilitySideEffectClass.None, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
    }
}
