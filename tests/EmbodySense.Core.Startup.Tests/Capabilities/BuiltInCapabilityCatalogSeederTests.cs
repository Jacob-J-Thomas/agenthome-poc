using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions _catalogArtifactJsonOptions = CreateCatalogJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _catalogHashJsonOptions = CreateCatalogJsonOptions(writeIndented: false);

    [Fact]
    public void Catalog_declares_one_exact_non_effecting_local_model_inference_graph_node()
    {
        var descriptor = Assert.Single(
            BuiltInCapabilityCatalog.Descriptors,
            item => item.Id.Value == "org.embodysense/model-inference");

        Assert.Equal(CapabilityDescriptor.CurrentSchemaVersion, descriptor.SchemaVersion);
        Assert.Equal("1.0.0", descriptor.Version.Value);
        Assert.Equal(CapabilityKind.GraphNode, descriptor.Kind);
        Assert.Equal("org.embodysense", descriptor.Implementation.ProviderId.Value);
        Assert.Equal("model-inference", descriptor.Implementation.ImplementationId);
        Assert.Equal(CapabilitySideEffectClass.None, descriptor.SideEffectClass);
        Assert.Equal(CapabilityEgressMode.None, descriptor.Requirements.EgressMode);
        Assert.Empty(descriptor.Requirements.DataClasses);
        Assert.Empty(descriptor.Requirements.EgressDestinations);
        Assert.Empty(descriptor.Requirements.Secrets);
    }

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
        await SeedAuthenticatedCatalogPagesAsync(paths, provider, service, template);

        var firstPage = await service.ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        Assert.Equal(CapabilityCatalogReadStatus.Available, firstPage.Status);
        Assert.Equal(CapabilityCatalogLimits.MaximumPageSize, firstPage.Page!.Entries.Count);
        Assert.NotNull(firstPage.Page.NextCursor);
        var secondPage = await service.ReadAsync(firstPage.Page.NextCursor, CapabilityCatalogLimits.MaximumPageSize);
        Assert.Equal(CapabilityCatalogReadStatus.Available, secondPage.Status);
        Assert.Single(secondPage.Page!.Entries);

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

    private static async Task SeedAuthenticatedCatalogPagesAsync(WorkspacePaths paths, FileCapabilityCatalogTrustProvider provider, CapabilityCatalogService service, CapabilityDescriptor template)
    {
        // Pagination is the behavior under test. Bootstrap one real catalog generation, then prepare the remaining
        // authenticated fixture as one direct successor so setup does not perform 101 unrelated durable mutations.
        Assert.True(CapabilityId.TryParse("aaa.example/capability-000", out var firstId, out _));
        var declared = await service.DeclareAsync(template with { Id = firstId! }, 0, "declare-prefill-000");
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, declared.Status);

        var primaryJson = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        var current = JsonSerializer.Deserialize<CapabilityCatalogFixtureDocument>(primaryJson, _catalogArtifactJsonOptions)!;
        var sourceEntry = Assert.Single(current.Entries);
        var entries = new List<CapabilityCatalogFixtureEntry>();
        for (var index = 0; index <= CapabilityCatalogLimits.MaximumPageSize; index++)
        {
            Assert.True(CapabilityId.TryParse($"aaa.example/capability-{index:D3}", out var id, out _));
            Assert.True(CapabilityDescriptorJson.TrySerialize(template with { Id = id! }, out var descriptorJson, out _));
            entries.Add(sourceEntry with { DescriptorJson = descriptorJson!, LastOperationId = $"declare-prefill-{index:D3}" });
        }

        var candidateGeneration = checked(current.Generation + 1);
        var candidate = current with { Generation = candidateGeneration, CatalogRevision = entries.Count, Entries = entries, ContentDigest = string.Empty, AuthenticationTag = string.Empty };
        var contentDigest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(candidate, _catalogHashJsonOptions))).Value;
        var authenticationTag = await provider.AuthenticateArtifactAsync(current.WorkspaceIdentity, candidateGeneration, contentDigest);
        candidate = candidate with { ContentDigest = contentDigest, AuthenticationTag = authenticationTag };

        File.Copy(paths.CapabilityCatalogDocumentPath, paths.CapabilityCatalogProofPath, overwrite: true);
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, JsonSerializer.Serialize(candidate, _catalogArtifactJsonOptions) + Environment.NewLine);
        _ = await provider.AdvanceAsync(current.WorkspaceIdentity, current.Generation, current.ContentDigest, candidateGeneration, contentDigest);
    }

    private static JsonSerializerOptions CreateCatalogJsonOptions(bool writeIndented)
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
        };
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

    private sealed record CapabilityCatalogFixtureDocument(
        int SchemaVersion,
        string WorkspaceIdentity,
        long Generation,
        long CatalogRevision,
        IReadOnlyList<CapabilityCatalogFixtureEntry> Entries,
        IReadOnlyList<CapabilityCatalogFixtureOperation> Operations,
        string ContentDigest,
        string AuthenticationTag);

    private sealed record CapabilityCatalogFixtureEntry(
        string DescriptorJson,
        long Revision,
        CapabilityDeclarationState Declaration,
        CapabilityInstallationState Installation,
        CapabilityEnablementState Enablement,
        CapabilityHealthState Health,
        CapabilityRetirementState Retirement,
        CapabilityTrustState Trust,
        DateTimeOffset UpdatedAtUtc,
        string LastOperationId);

    private sealed record CapabilityCatalogFixtureOperation(
        string OperationId,
        string RequestHash,
        CapabilityCatalogMutationStatus Outcome,
        long CatalogRevision,
        string CapabilityId,
        long EntryRevision,
        CapabilityDeclarationState Declaration,
        CapabilityInstallationState Installation,
        CapabilityEnablementState Enablement,
        CapabilityHealthState Health,
        CapabilityRetirementState Retirement,
        CapabilityTrustState Trust,
        DateTimeOffset UpdatedAtUtc,
        string LastOperationId);
}
