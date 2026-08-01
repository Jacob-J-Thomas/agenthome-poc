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
    public void Host_runtime_exposes_one_exact_compatible_bounded_context()
    {
        Assert.False(CapabilityHostRuntime.Platform.Equals(CapabilityPlatform.Any));
        Assert.All(BuiltInCapabilityCatalog.Descriptors, descriptor =>
        {
            Assert.True(descriptor.Compatibility.HostVersionRange.Contains(CapabilityHostRuntime.HostContractVersion));
            Assert.Contains(descriptor.Compatibility.SupportedPlatforms, platform => platform.Equals(CapabilityPlatform.Any) || platform.Equals(CapabilityHostRuntime.Platform));
        });
    }

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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Reseed_resumes_only_a_contiguous_durable_builtin_bootstrap_prefix(int committedStageCount)
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, provider));
        var descriptor = BuiltInCapabilityCatalog.Descriptors[0];
        await CommitBootstrapPrefixAsync(service, descriptor, committedStageCount);

        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);

        var resumed = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).Single(entry => entry.Descriptor.Id.Equals(descriptor.Id));
        AssertEffectReady(resumed);
    }

    [Fact]
    public async Task Reseed_preserves_an_operator_no_change_decision_after_a_partial_builtin_declaration()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, provider));
        var descriptor = BuiltInCapabilityCatalog.Descriptors[0];
        await CommitBootstrapPrefixAsync(service, descriptor, 1);
        var disabled = await service.DisableAsync(descriptor.Id, await ReadCatalogRevisionAsync(service), "operator-preserve-partial-disabled");
        Assert.Equal(CapabilityCatalogMutationStatus.NoChange, disabled.Status);

        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);

        var preserved = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).Single(entry => entry.Descriptor.Id.Equals(descriptor.Id));
        Assert.Equal(CapabilityInstallationState.NotInstalled, preserved.Lifecycle.Installation);
        Assert.Equal(CapabilityEnablementState.Disabled, preserved.Lifecycle.Enablement);
        Assert.Equal(CapabilityTrustState.Unverified, preserved.Lifecycle.Trust);
        Assert.Equal(CapabilityHealthState.Unknown, preserved.Lifecycle.Health);
        Assert.Equal($"builtin-declare-{descriptor.Id.Value.Replace('/', '-')}-v1", preserved.LastOperationId);
    }

    [Fact]
    public async Task Seed_reproves_bootstrap_receipts_after_each_transaction_and_stops_for_an_interleaved_operator_touch()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, provider));
        var descriptor = BuiltInCapabilityCatalog.Descriptors[0];
        var declareOperationId = $"builtin-declare-{descriptor.Id.Value.Replace('/', '-')}-v1";
        CapabilityCatalogMutationResult? operatorTouch = null;
        var observer = new InterleavingBuiltInCapabilityCatalogSeedObserver(declareOperationId, async (_, cancellationToken) =>
        {
            operatorTouch = await service.DisableAsync(descriptor.Id, await ReadCatalogRevisionAsync(service), "operator-touch-between-bootstrap-stages", cancellationToken);
        });

        await new BuiltInCapabilityCatalogSeeder(provider, observer).SeedAsync(paths);

        Assert.Equal(1, observer.InterleavingCount);
        Assert.Equal(CapabilityCatalogMutationStatus.NoChange, Assert.IsType<CapabilityCatalogMutationResult>(operatorTouch).Status);
        var preserved = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).Single(entry => entry.Descriptor.Id.Equals(descriptor.Id));
        Assert.Equal(CapabilityInstallationState.NotInstalled, preserved.Lifecycle.Installation);
        Assert.Equal(CapabilityEnablementState.Disabled, preserved.Lifecycle.Enablement);
        Assert.Equal(CapabilityTrustState.Unverified, preserved.Lifecycle.Trust);
        Assert.Equal(CapabilityHealthState.Unknown, preserved.Lifecycle.Health);
        var receipts = await new CapabilityCatalogStore(paths, provider).ReadOperationReceiptsAsync(descriptor.Id);
        Assert.Equal(CapabilityCatalogReadStatus.Available, receipts.Status);
        Assert.Equal([declareOperationId, "operator-touch-between-bootstrap-stages"], receipts.Receipts.Select(receipt => receipt.OperationId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reseed_preserves_an_exact_builtin_declared_by_an_operator()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, provider));
        var descriptor = BuiltInCapabilityCatalog.Descriptors[0];
        var declared = await service.DeclareAsync(descriptor, 0, "operator-declare-exact-builtin");
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, declared.Status);

        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);

        var preserved = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).Single(entry => entry.Descriptor.Id.Equals(descriptor.Id));
        Assert.Equal(CapabilityInstallationState.NotInstalled, preserved.Lifecycle.Installation);
        Assert.Equal(CapabilityEnablementState.Disabled, preserved.Lifecycle.Enablement);
        Assert.Equal(CapabilityTrustState.Unverified, preserved.Lifecycle.Trust);
        Assert.Equal(CapabilityHealthState.Unknown, preserved.Lifecycle.Health);
        Assert.Equal("operator-declare-exact-builtin", preserved.LastOperationId);
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
        var transitioned = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).Single(candidate => candidate.Descriptor.Id.Equals(entry.Descriptor.Id));
        assertLifecycle(transitioned.Lifecycle);

        await seeder.SeedAsync(paths);

        var reseeded = (await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider))).Single(candidate => candidate.Descriptor.Id.Equals(entry.Descriptor.Id));
        assertLifecycle(reseeded.Lifecycle);
        Assert.Equal(transitioned.Revision, reseeded.Revision);
    }

    private static void AssertEffectReady(CapabilityCatalogEntry entry)
    {
        Assert.Equal(CapabilityDeclarationState.Declared, entry.Lifecycle.Declaration);
        Assert.Equal(CapabilityInstallationState.Installed, entry.Lifecycle.Installation);
        Assert.Equal(CapabilityEnablementState.Enabled, entry.Lifecycle.Enablement);
        Assert.Equal(CapabilityHealthState.Healthy, entry.Lifecycle.Health);
        Assert.Equal(CapabilityTrustState.Verified, entry.Lifecycle.Trust);
    }

    private static async Task CommitBootstrapPrefixAsync(CapabilityCatalogService service, CapabilityDescriptor descriptor, int committedStageCount)
    {
        var operationPrefix = descriptor.Id.Value.Replace('/', '-');
        var declared = await service.DeclareAsync(descriptor, 0, $"builtin-declare-{operationPrefix}-v1");
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, declared.Status);
        var revision = declared.CatalogRevision!.Value;
        if (committedStageCount >= 2)
        {
            var installed = await service.InstallAsync(descriptor.Id, revision, $"builtin-install-{operationPrefix}-v1");
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, installed.Status);
            revision = installed.CatalogRevision!.Value;
        }
        if (committedStageCount >= 3)
        {
            var verified = await service.VerifyAsync(descriptor.Id, revision, $"builtin-verify-{operationPrefix}-v1");
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, verified.Status);
            revision = verified.CatalogRevision!.Value;
        }
        if (committedStageCount >= 4)
        {
            var enabled = await service.EnableAsync(descriptor.Id, revision, $"builtin-enable-{operationPrefix}-v1");
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, enabled.Status);
        }
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
