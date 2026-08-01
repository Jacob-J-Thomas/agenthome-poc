using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

public sealed class BuiltInCapabilityCatalogSeederTests
{
    [Fact]
    public async Task Fresh_seed_bootstraps_effect_ready_built_ins_without_assignment_or_authority()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);

        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);

        var entries = await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider));
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, entries.Count);
        Assert.All(entries, AssertEffectReady);
    }

    [Fact]
    public async Task Concurrent_seeders_converge_to_effect_ready_built_ins_without_assignment_or_authority()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var seeders = Enumerable.Range(0, 8).Select(_ => new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths));

        await Task.WhenAll(seeders);

        var entries = await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider));
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(CapabilityDeclarationState.Declared, entry.Lifecycle.Declaration);
            Assert.Equal(CapabilityInstallationState.Installed, entry.Lifecycle.Installation);
            Assert.Equal(CapabilityEnablementState.Enabled, entry.Lifecycle.Enablement);
            Assert.Equal(CapabilityHealthState.Healthy, entry.Lifecycle.Health);
            Assert.Equal(CapabilityTrustState.Verified, entry.Lifecycle.Trust);
        });
        var artifact = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        Assert.DoesNotContain("assignment", artifact, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authority", artifact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Seeder_finds_existing_built_ins_beyond_the_first_bounded_page()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, provider));
        var template = BuiltInCapabilityCatalog.Descriptors[0];
        var revision = 0L;
        for (var index = 0; index <= CapabilityCatalogLimits.MaximumPageSize; index++)
        {
            _ = CapabilityId.TryParse($"aaa.example/capability-{index:D3}", out var id, out _);
            var result = await service.DeclareAsync(template with { Id = id! }, revision, $"declare-prefill-{index:D3}");
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, result.Status);
            revision = result.CatalogRevision!.Value;
        }

        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);
        var firstArtifact = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);

        Assert.Equal(firstArtifact, await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath));
        var builtIns = await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider));
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, builtIns.Count);
        Assert.All(builtIns, entry =>
        {
            Assert.Equal(CapabilityInstallationState.Installed, entry.Lifecycle.Installation);
            Assert.Equal(CapabilityEnablementState.Enabled, entry.Lifecycle.Enablement);
            Assert.Equal(CapabilityHealthState.Healthy, entry.Lifecycle.Health);
            Assert.Equal(CapabilityTrustState.Verified, entry.Lifecycle.Trust);
        });
    }

    [Fact]
    public async Task Reseed_preserves_an_operator_disabled_built_in()
    {
        await AssertReseedPreservesLifecycleAsync(
            (service, id, revision) => service.DisableAsync(id, revision, "operator-disable"),
            lifecycle => Assert.Equal(CapabilityEnablementState.Disabled, lifecycle.Enablement));
    }

    [Fact]
    public async Task Reseed_preserves_a_rejected_built_in_trust_decision()
    {
        await AssertReseedPreservesLifecycleAsync(
            (service, id, revision) => service.RejectTrustAsync(id, revision, "operator-reject-trust"),
            lifecycle => Assert.Equal(CapabilityTrustState.Rejected, lifecycle.Trust));
    }

    [Theory]
    [InlineData(CapabilityHealthState.Degraded)]
    [InlineData(CapabilityHealthState.Unavailable)]
    public async Task Reseed_preserves_a_nonhealthy_built_in_observation(CapabilityHealthState health)
    {
        await AssertReseedPreservesLifecycleAsync(
            (service, id, revision) => health == CapabilityHealthState.Degraded
                ? service.MarkDegradedAsync(id, revision, "operator-mark-degraded")
                : service.MarkUnavailableAsync(id, revision, "operator-mark-unavailable"),
            lifecycle => Assert.Equal(health, lifecycle.Health));
    }

    private static async Task AssertReseedPreservesLifecycleAsync(Func<CapabilityCatalogService, CapabilityId, long, Task<CapabilityCatalogMutationResult>> transition, Action<CapabilityLifecycleSnapshot> assertLifecycle)
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var seeder = new BuiltInCapabilityCatalogSeeder(provider);
        await seeder.SeedAsync(paths);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, provider));
        var entry = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).First();

        var transitionResult = await transition(service, entry.Descriptor.Id, await ReadCatalogRevisionAsync(service));
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, transitionResult.Status);

        await seeder.SeedAsync(paths);

        var reseeded = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).Single(candidate => candidate.Descriptor.Id.Equals(entry.Descriptor.Id));
        assertLifecycle(reseeded.Lifecycle);
        Assert.Equal(transitionResult.CatalogRevision, reseeded.Revision);
    }

    private static void AssertEffectReady(CapabilityCatalogEntry entry)
    {
        Assert.Equal(CapabilityDeclarationState.Declared, entry.Lifecycle.Declaration);
        Assert.Equal(CapabilityInstallationState.Installed, entry.Lifecycle.Installation);
        Assert.Equal(CapabilityEnablementState.Enabled, entry.Lifecycle.Enablement);
        Assert.Equal(CapabilityHealthState.Healthy, entry.Lifecycle.Health);
        Assert.Equal(CapabilityTrustState.Verified, entry.Lifecycle.Trust);
    }

    private static async Task<long> ReadCatalogRevisionAsync(CapabilityCatalogService service)
    {
        var read = await service.ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        Assert.Equal(CapabilityCatalogReadStatus.Available, read.Status);
        return read.Page!.CatalogRevision;
    }

    private static async Task<IReadOnlyList<CapabilityCatalogEntry>> ReadBuiltInsAsync(CapabilityCatalogStore store)
    {
        var entries = new List<CapabilityCatalogEntry>();
        string? cursor = null;
        do
        {
            var read = await store.ReadAsync(cursor, CapabilityCatalogLimits.MaximumPageSize);
            Assert.Equal(CapabilityCatalogReadStatus.Available, read.Status);
            entries.AddRange(read.Page!.Entries.Where(entry => entry.Descriptor.Id.Value.StartsWith("org.embodysense/", StringComparison.Ordinal)));
            cursor = read.Page.NextCursor;
        }
        while (cursor is not null);

        return entries;
    }
}
