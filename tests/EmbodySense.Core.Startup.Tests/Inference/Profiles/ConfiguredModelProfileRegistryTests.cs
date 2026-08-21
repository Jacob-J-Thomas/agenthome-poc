using System.Text.Json;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Inference.Profiles.Models;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Startup.Tests.Loops.Execution;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Inference.Profiles;

public sealed class ConfiguredModelProfileRegistryTests
{
    [Fact]
    public async Task Registry_projects_an_unavailable_remote_posture_when_its_finite_output_ceiling_cannot_be_hard_enforced()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.File("codex-runtime-one");
        await File.WriteAllTextAsync(executable, "exact runtime content one");
        var first = Registry(workspace, executable, "codex-cli 1.2.3");
        var profileId = ProfileId();

        var read = await first.ReadAsync(profileId);
        var metadata = Assert.IsType<GovernedModelProfileMetadata>(read.Metadata);
        var posture = await first.ReadPostureAsync(metadata);

        Assert.Equal(ModelProfileSourceReadStatus.Found, read.Status);
        Assert.Equal(GovernedModelLocality.Remote, metadata.Privacy.Locality);
        Assert.Equal(CapabilityEgressMode.Unrestricted, metadata.Privacy.Egress);
        Assert.Equal(["sensitive"], metadata.Privacy.AcceptedDataClasses.Select(value => value.Value));
        Assert.Empty(metadata.Privacy.Regions);
        Assert.Equal(GovernedModelRetentionPosture.Indefinite, metadata.Privacy.Retention);
        Assert.Equal(GovernedModelTrainingPosture.Allowed, metadata.Privacy.Training);
        Assert.Equal(GovernedModelUsageSupport.AuthoritativeAfterDispatch, metadata.UsageSupport.InputTokens);
        Assert.Equal(GovernedModelUsageSupport.AuthoritativeAfterDispatch, metadata.UsageSupport.OutputTokens);
        Assert.Equal(GovernedModelUsageSupport.AuthoritativeAfterDispatch, metadata.UsageSupport.CachedTokens);
        Assert.Equal(GovernedModelUsageSupport.AuthoritativeAfterDispatch, metadata.UsageSupport.TotalTokens);
        Assert.Equal(GovernedModelUsageSupport.Unavailable, metadata.UsageSupport.MonetaryCost);
        Assert.Equal(1, metadata.ContextWindowTokens);
        Assert.Equal(1, metadata.MaximumOutputTokens);
        Assert.Equal(ModelProfileAdapterPostureStatus.Unavailable, posture.Status);
        Assert.DoesNotContain(executable, JsonSerializer.Serialize(metadata), StringComparison.Ordinal);

        var changedVersion = Registry(workspace, executable, "codex-cli 1.2.4");
        var changedMetadata = Assert.IsType<GovernedModelProfileMetadata>((await changedVersion.ReadAsync(profileId)).Metadata);
        Assert.NotEqual(metadata.ConfigurationHash, changedMetadata.ConfigurationHash);

        await File.WriteAllTextAsync(executable, "exact runtime content two");
        Assert.Equal(ModelProfileSourceReadStatus.Unavailable, (await first.ReadAsync(profileId)).Status);
        Assert.Equal(ModelProfileDefaultReadStatus.Unavailable, (await first.ReadAsync()).Status);
        Assert.Equal(ModelProfileAdapterPostureStatus.Unavailable, (await first.ReadPostureAsync(metadata)).Status);
        var changedContent = Registry(workspace, executable, "codex-cli 1.2.3");
        Assert.NotEqual(metadata.ConfigurationHash, (await changedContent.ReadAsync(profileId)).Metadata?.ConfigurationHash);
    }

    [Fact]
    public async Task Catalog_facade_marks_the_configured_profile_unavailable_when_its_output_ceiling_cannot_be_hard_enforced()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executable = workspace.File("codex-runtime-catalog");
        await File.WriteAllTextAsync(executable, "catalog runtime content");
        var registry = Registry(workspace, executable, "codex-cli catalog");
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var service = new ModelProfileCatalogService(new CapabilityCatalogStore(paths, trust), registry, registry);

        var available = await new ModelProfileCatalogFacade(service, registry).ReadAsync(null, 50);
        var source = await registry.ReadAsync();

        Assert.Equal("available", available.Status);
        Assert.Null(available.DefaultProfileId);
        var item = Assert.Single(available.Profiles, value => value.ProfileId == ProfileId().Value);
        var metadata = Assert.IsType<GovernedModelProfileMetadata>(item.Metadata);
        var exactPolicy = ExactPolicy(metadata);
        Assert.Equal("adapterunavailable", item.AvailabilityReason);
        Assert.Null(item.RecommendedExactPolicy);
        Assert.Null(item.ExactProfilePin);

        var exactPreview = await new ModelProfileCatalogFacade(service, registry).PreviewAsync(
            new ModelProfileRoutingPreviewInput(exactPolicy, "default", "provider-inference", ["sensitive"]));
        Assert.Equal("ineligible", exactPreview.Status);
        Assert.True(exactPreview.AdmissionRequired);
        Assert.Null(exactPreview.PolicyHash);
        Assert.Null(exactPreview.Primary);
        Assert.Empty(exactPreview.Fallbacks);

        var publicData = await new ModelProfileCatalogFacade(service, registry).PreviewAsync(
            new ModelProfileRoutingPreviewInput(exactPolicy, "default", "provider-inference", ["public"]));
        Assert.Equal("ineligible", publicData.Status);

        var inheritPolicy = GovernedModelRoutingPolicy.Create(
            1,
            GovernedModelRoutingSelector.Inherit([ProfileId()]),
            [],
            exactPolicy.Requirements);
        var inherit = await new ModelProfileCatalogFacade(service, registry).PreviewAsync(
            new ModelProfileRoutingPreviewInput(inheritPolicy, "default", "provider-inference", ["sensitive"]));
        Assert.Equal("unavailable", inherit.Status);
        Assert.Null(inherit.ResolvedDefaultProfileId);

        var forgedRevision = new FixedDefaultSource(
            new ModelProfileDefaultReadResult(ModelProfileDefaultReadStatus.Found, source.ProfileId, new string('0', 64)));
        var forged = await new ModelProfileCatalogFacade(service, forgedRevision).ReadAsync(null, 50);
        Assert.Equal("available", forged.Status);
        Assert.Null(forged.DefaultProfileId);
        Assert.Contains(forged.Profiles, value => value.ProfileId == ProfileId().Value && value.AvailabilityReason == "adapterunavailable");
        var forgedPreview = await new ModelProfileCatalogFacade(service, forgedRevision).PreviewAsync(
            new ModelProfileRoutingPreviewInput(inheritPolicy, "default", "provider-inference", ["sensitive"]));
        Assert.Equal("unavailable", forgedPreview.Status);

        var notConfiguredSource = new FixedDefaultSource(
            new ModelProfileDefaultReadResult(ModelProfileDefaultReadStatus.NotConfigured, null, null));
        var notConfigured = await new ModelProfileCatalogFacade(service, notConfiguredSource).ReadAsync(null, 50);
        Assert.Equal("available", notConfigured.Status);
        Assert.Null(notConfigured.DefaultProfileId);
        Assert.Contains(notConfigured.Profiles, value => value.ProfileId == ProfileId().Value && value.AvailabilityReason == "adapterunavailable");
        var exactWithoutDefault = await new ModelProfileCatalogFacade(service, notConfiguredSource).PreviewAsync(
            new ModelProfileRoutingPreviewInput(exactPolicy, "default", "provider-inference", ["sensitive"]));
        Assert.Equal("ineligible", exactWithoutDefault.Status);
        var inheritWithoutDefault = await new ModelProfileCatalogFacade(service, notConfiguredSource).PreviewAsync(
            new ModelProfileRoutingPreviewInput(inheritPolicy, "default", "provider-inference", ["sensitive"]));
        Assert.Equal("ineligible", inheritWithoutDefault.Status);

        Assert.True(CapabilityId.TryParse("org.example/missing-profile", out var missing, out _));
        var unknown = await new ModelProfileCatalogFacade(
            service,
            new FixedDefaultSource(new ModelProfileDefaultReadResult(ModelProfileDefaultReadStatus.Found, missing, source.SourceRevisionHash)))
            .ReadAsync(null, 50);
        Assert.Equal("available", unknown.Status);
        Assert.Null(unknown.DefaultProfileId);
        Assert.Contains(unknown.Profiles, value => value.ProfileId == ProfileId().Value && value.AvailabilityReason == "adapterunavailable");
    }

    [Fact]
    public async Task Composite_sources_support_multiple_distinct_adapters_but_fail_closed_on_duplicate_ownership()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.File("codex-runtime-composite");
        await File.WriteAllTextAsync(executable, "composite runtime content");
        var registry = Registry(workspace, executable, "codex-cli composite");
        var profileId = ProfileId();
        var expected = await registry.ReadAsync(profileId);
        var metadata = Assert.IsType<GovernedModelProfileMetadata>(expected.Metadata);
        var absentMetadata = new FixedMetadataSource(new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.NotFound, null, null));
        var absentAdapter = new FixedAdapterRegistry(new ModelProfileAdapterPosture(
            ModelProfileAdapterPostureStatus.Unregistered,
            metadata.ContentHash,
            new string('1', 64)));

        var selected = await new CompositeModelProfileMetadataSource([absentMetadata, registry]).ReadAsync(profileId);
        var selectedAdapter = await new CompositeModelProfileAdapterRegistry([absentAdapter, registry]).ReadPostureAsync(metadata);
        Assert.Equal(ModelProfileSourceReadStatus.Found, selected.Status);
        Assert.Equal(expected.SourceRevisionHash, selected.SourceRevisionHash);
        Assert.Equal(ModelProfileAdapterPostureStatus.Unavailable, selectedAdapter.Status);

        var alternateMetadata = GovernedModelProfileMetadata.Create(
            1,
            metadata.DescriptorIdentity,
            metadata.ProviderId,
            metadata.AdapterId,
            "alternate-model",
            metadata.AdapterContractVersion,
            metadata.ConfigurationRevision,
            new string('2', 64),
            metadata.PublicPurpose,
            metadata.Modalities,
            metadata.Capabilities,
            metadata.ContextWindowTokens,
            metadata.MaximumOutputTokens,
            metadata.Privacy,
            metadata.UsageSupport,
            metadata.PermittedRoleIds,
            metadata.PermittedNodeTypeIds);
        var firstAdapter = new SelectingAdapterRegistry(metadata.ContentHash, new string('3', 64));
        var secondAdapter = new SelectingAdapterRegistry(alternateMetadata.ContentHash, new string('4', 64));
        var multiAdapter = new CompositeModelProfileAdapterRegistry([firstAdapter, secondAdapter]);
        var firstPosture = await multiAdapter.ReadPostureAsync(metadata);
        var secondPosture = await multiAdapter.ReadPostureAsync(alternateMetadata);
        Assert.Equal(ModelProfileAdapterPostureStatus.Ready, firstPosture.Status);
        Assert.Equal(ModelProfileAdapterPostureStatus.Ready, secondPosture.Status);
        Assert.Equal(firstPosture.RegistryRevisionHash, secondPosture.RegistryRevisionHash);

        var duplicated = await new CompositeModelProfileMetadataSource([registry, registry]).ReadAsync(profileId);
        var duplicatedAdapter = await new CompositeModelProfileAdapterRegistry([registry, registry]).ReadPostureAsync(metadata);
        Assert.Equal(ModelProfileSourceReadStatus.Unavailable, duplicated.Status);
        Assert.Equal(ModelProfileAdapterPostureStatus.Unavailable, duplicatedAdapter.Status);
    }

    [Fact]
    public async Task Composite_resolver_selects_one_exact_owner_and_disposes_every_ambiguous_or_unavailable_lease()
    {
        var request = ResolverRequest();
        var selectedLease = new RecordingLease(request);
        var selected = await new CompositeExactModelProfileInferenceClientResolver(
            [new FixedResolver(ExactModelProfileInferenceClientResolutionStatus.Ineligible), new FixedResolver(selectedLease)])
            .ResolveAsync(request);

        Assert.Equal(ExactModelProfileInferenceClientResolutionStatus.Resolved, selected.Status);
        Assert.Same(selectedLease, selected.Lease);
        Assert.False(selectedLease.Disposed);

        var duplicateOne = new RecordingLease(request);
        var duplicateTwo = new RecordingLease(request);
        var duplicated = await new CompositeExactModelProfileInferenceClientResolver(
            [new FixedResolver(duplicateOne), new FixedResolver(duplicateTwo)])
            .ResolveAsync(request);
        Assert.Equal(ExactModelProfileInferenceClientResolutionStatus.Unavailable, duplicated.Status);
        Assert.Null(duplicated.Lease);
        Assert.True(duplicateOne.Disposed);
        Assert.True(duplicateTwo.Disposed);

        var unavailableLease = new RecordingLease(request);
        var unavailable = await new CompositeExactModelProfileInferenceClientResolver(
            [new FixedResolver(unavailableLease), new FixedResolver(ExactModelProfileInferenceClientResolutionStatus.Unavailable)])
            .ResolveAsync(request);
        Assert.Equal(ExactModelProfileInferenceClientResolutionStatus.Unavailable, unavailable.Status);
        Assert.True(unavailableLease.Disposed);
    }

    [Fact]
    public async Task Runtime_composition_exposes_multiple_replaceable_profiles_and_routes_one_exact_owner()
    {
        using var workspace = new TestWorkspace();
        var executable = workspace.File("codex-runtime-composition");
        await File.WriteAllTextAsync(executable, "composition runtime content");
        var registry = Registry(workspace, executable, "codex-cli composition");
        var configuredId = ProfileId();
        var configuredRead = await registry.ReadAsync(configuredId);
        var configuredMetadata = Assert.IsType<GovernedModelProfileMetadata>(configuredRead.Metadata);
        Assert.True(CapabilityId.TryParse("org.example/model-secondary", out var secondaryId, out _));
        var descriptor = BuiltInCapabilityCatalog.Descriptors.Single(value => value.Id.Equals(configuredId)) with
        {
            Id = secondaryId!,
            Purpose = "A secondary server-owned model profile."
        };
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var secondaryIdentity, out var descriptorValidation),
            string.Join(';', descriptorValidation.Errors.Select(error => error.Message)));
        var secondaryMetadata = GovernedModelProfileMetadata.Create(
            1,
            secondaryIdentity!,
            "org.example",
            "secondary-adapter",
            "secondary-model",
            "v1",
            1,
            new string('2', 64),
            descriptor.Purpose,
            configuredMetadata.Modalities,
            configuredMetadata.Capabilities,
            1,
            1,
            configuredMetadata.Privacy,
            configuredMetadata.UsageSupport,
            configuredMetadata.PermittedRoleIds,
            configuredMetadata.PermittedNodeTypeIds);
        var request = ResolverRequest();
        var selectedLease = new RecordingLease(request);
        var configuredProvider = new ModelProfileRuntimeProvider(
            new SelectingMetadataSource(configuredId, configuredRead),
            new SelectingAdapterRegistry(configuredMetadata.ContentHash, new string('3', 64)),
            _ => new FixedResolver(selectedLease));
        var secondaryProvider = new ModelProfileRuntimeProvider(
            new SelectingMetadataSource(
                secondaryId!,
                new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.Found, secondaryMetadata, new string('4', 64))),
            new SelectingAdapterRegistry(secondaryMetadata.ContentHash, new string('5', 64)),
            _ => new FixedResolver(ExactModelProfileInferenceClientResolutionStatus.Ineligible));

        var composition = ModelProfileRuntimeComposition.Create(configuredProvider, [secondaryProvider]);

        Assert.Equal(configuredMetadata.ContentHash, (await composition.MetadataSource.ReadAsync(configuredId)).Metadata?.ContentHash);
        Assert.Equal(secondaryMetadata.ContentHash, (await composition.MetadataSource.ReadAsync(secondaryId!)).Metadata?.ContentHash);
        Assert.Equal(ModelProfileAdapterPostureStatus.Ready, (await composition.AdapterRegistry.ReadPostureAsync(configuredMetadata)).Status);
        Assert.Equal(ModelProfileAdapterPostureStatus.Ready, (await composition.AdapterRegistry.ReadPostureAsync(secondaryMetadata)).Status);
        var resolution = await composition.ClientResolver.ResolveAsync(request);
        Assert.Same(selectedLease, resolution.Lease);
        Assert.False(selectedLease.Disposed);
        await selectedLease.DisposeAsync();
    }

    [Fact]
    public async Task Configured_resolver_rejects_the_unforwardable_output_ceiling_before_snapshot_or_provider_dispatch()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "test-model");
        var options = Options(workspace, executable);
        var status = RuntimeStatus(executable, options.Model!, "codex-cli compatible-test");
        var registry = new ConfiguredModelProfileRegistry(options, status);
        var request = await ConfiguredResolverRequestAsync(registry);
        var resolver = new ConfiguredModelProfileInferenceClientResolver(options, registry);
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "embodysense-model-profile-snapshots");
        var priorSnapshots = Directory.Exists(snapshotRoot)
            ? Directory.GetDirectories(snapshotRoot).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var resolution = await resolver.ResolveAsync(request);
        Assert.Equal(ExactModelProfileInferenceClientResolutionStatus.Ineligible, resolution.Status);
        Assert.Null(resolution.Lease);
        Assert.Empty(Directory.Exists(snapshotRoot)
            ? Directory.GetDirectories(snapshotRoot).Where(path => !priorSnapshots.Contains(path))
            : []);
    }

    [Fact]
    public async Task Test_only_exact_adapter_can_snapshot_the_bounded_npm_package_tree_without_using_the_unavailable_production_adapter()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "test-model");
        var packageRoot = Path.GetDirectoryName(executable)!;
        var entryScript = Path.Combine(packageRoot, "codex.js");
        var vendorDirectory = Path.Combine(packageRoot, "vendor", "host-platform");
        Directory.CreateDirectory(vendorDirectory);
        var vendorScript = Path.Combine(vendorDirectory, "codex-runtime.js");
        File.Move(entryScript, vendorScript);
        await File.WriteAllTextAsync(entryScript, "require('./vendor/host-platform/codex-runtime.js');\n");
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "package.json"), "{\"name\":\"@openai/codex\",\"private\":true}\n");
        var options = Options(workspace, executable);
        var registry = new ConfiguredModelProfileRegistry(options, RuntimeStatus(executable, options.Model!, "codex-cli npm-layout"));
        var request = await ConfiguredResolverRequestAsync(registry);
        var metadata = Assert.IsType<GovernedModelProfileMetadata>((await registry.ReadAsync(ProfileId())).Metadata);
        var posture = await registry.ReadPostureAsync(metadata);
        var resolver = new ConfiguredModelProfileInferenceClientResolver(
            options,
            registry,
            new FixedAdapterRegistry(new ModelProfileAdapterPosture(ModelProfileAdapterPostureStatus.Ready, metadata.ContentHash, posture.RegistryRevisionHash)));
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "embodysense-model-profile-snapshots");
        var priorSnapshots = Directory.Exists(snapshotRoot)
            ? Directory.GetDirectories(snapshotRoot).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var resolution = await resolver.ResolveAsync(request);
        Assert.Equal(ExactModelProfileInferenceClientResolutionStatus.Resolved, resolution.Status);
        await using var lease = Assert.IsAssignableFrom<IExactModelProfileInferenceClientLease>(resolution.Lease);
        var snapshot = Assert.Single(Directory.GetDirectories(snapshotRoot), path => !priorSnapshots.Contains(path));
        Assert.True(File.Exists(Path.Combine(snapshot, "package.json")));
        Assert.True(File.Exists(Path.Combine(snapshot, "vendor", "host-platform", "codex-runtime.js")));
    }

    [Fact]
    public async Task Snapshot_lease_retains_the_package_tree_referenced_by_a_Windows_npm_shim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var shim = workspace.File("codex.cmd");
        var packageRoot = Path.Combine(workspace.RootPath, "node_modules", "@openai", "codex");
        var vendorDirectory = Path.Combine(packageRoot, "vendor", "host-platform");
        Directory.CreateDirectory(vendorDirectory);
        await File.WriteAllTextAsync(shim, "@node \"%~dp0node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n");
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "package.json"), "{\"name\":\"@openai/codex\",\"private\":true}\n");
        Directory.CreateDirectory(Path.Combine(packageRoot, "bin"));
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "bin", "codex.js"), "require('../vendor/host-platform/codex-runtime.js');\n");
        await File.WriteAllTextAsync(Path.Combine(vendorDirectory, "codex-runtime.js"), """
            const readline = require("node:readline");
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

            input.on("line", line => {
              const message = JSON.parse(line);
              if (message.method === "initialize") {
                process.stdout.write(`${JSON.stringify({ id: message.id, result: {} })}\n`);
              }
            });
            """);
        var options = Options(workspace, shim);
        var registry = new ConfiguredModelProfileRegistry(options, RuntimeStatus(shim, options.Model!, "codex-cli Windows npm shim"));
        var request = await ConfiguredResolverRequestAsync(registry);
        var metadata = Assert.IsType<GovernedModelProfileMetadata>((await registry.ReadAsync(ProfileId())).Metadata);
        var posture = await registry.ReadPostureAsync(metadata);
        var resolver = new ConfiguredModelProfileInferenceClientResolver(
            options,
            registry,
            new FixedAdapterRegistry(new ModelProfileAdapterPosture(ModelProfileAdapterPostureStatus.Ready, metadata.ContentHash, posture.RegistryRevisionHash)));
        var snapshotsDirectory = Path.Combine(Path.GetTempPath(), "embodysense-model-profile-snapshots");
        var priorSnapshots = Directory.Exists(snapshotsDirectory)
            ? Directory.GetDirectories(snapshotsDirectory).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var resolution = await resolver.ResolveAsync(request);
        Assert.Equal(ExactModelProfileInferenceClientResolutionStatus.Resolved, resolution.Status);
        await using var lease = Assert.IsAssignableFrom<IExactModelProfileInferenceClientLease>(resolution.Lease);
        var snapshotRoot = Assert.Single(Directory.GetDirectories(snapshotsDirectory), path => !priorSnapshots.Contains(path));

        Assert.True(File.Exists(Path.Combine(snapshotRoot, "node_modules", "@openai", "codex", "bin", "codex.js")));
        Assert.True(File.Exists(Path.Combine(snapshotRoot, "node_modules", "@openai", "codex", "vendor", "host-platform", "codex-runtime.js")));
    }

    private static ConfiguredModelProfileRegistry Registry(TestWorkspace workspace, string executable, string version)
    {
        var options = Options(workspace, executable);
        return new ConfiguredModelProfileRegistry(options, RuntimeStatus(executable, options.Model!, version));
    }

    private static LlmInferenceClientOptions Options(TestWorkspace workspace, string executable)
        => new()
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "test-model",
            WorkingDirectory = workspace.RootPath,
            CodexExecutablePath = executable,
            CodexSandbox = "read-only",
        };

    private static CodexRuntimeStatus RuntimeStatus(string executable, string model, string version)
        => new(
            CodexRuntimeCompatibility.Compatible,
            executable,
            executable,
            version,
            model,
            "test",
            "Exact test runtime evidence.");

    private static async Task<ExactModelProfileInferenceClientRequest> ConfiguredResolverRequestAsync(ConfiguredModelProfileRegistry registry)
    {
        var read = await registry.ReadAsync(ProfileId());
        var metadata = Assert.IsType<GovernedModelProfileMetadata>(read.Metadata);
        var posture = await registry.ReadPostureAsync(metadata);
        var descriptor = BuiltInCapabilityCatalog.Descriptors.Single(value => value.Id.Equals(ProfileId()));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var descriptorIdentity, out _));
        var primary = GovernedModelProfilePin.Create(
            new CapabilityAdmissionPin(
                descriptorIdentity!,
                descriptor.Kind,
                descriptor.Implementation,
                descriptor.Provenance,
                new CapabilityDependencyArtifactMetadata(null, null),
                descriptor.Purpose),
            metadata,
            Assert.IsType<string>(read.SourceRevisionHash),
            posture.RegistryRevisionHash);
        var policy = ExactPolicy(metadata);
        var baseline = ResolverRequest();
        var prior = baseline.AttemptIdentity;
        var identity = GovernedModelUsageLedgerIdentity.Create(
            1,
            prior.WorkspaceId,
            prior.RunId,
            prior.GraphId,
            prior.GraphRevisionId,
            prior.GraphExecutableHash,
            prior.ExecutionGeneration,
            prior.AdmissionReceiptHash,
            prior.RoutingAdmissionHash,
            prior.AuthorityEvidenceHash,
            prior.DataPostureEvidenceHash,
            prior.NodeId,
            prior.PlanOrdinal,
            prior.ActivationOrdinal,
            prior.VisitOrdinal,
            prior.AttemptOperationId,
            prior.AttemptNumber,
            primary.ContentHash,
            policy.Requirements.Budget.ContentHash);
        return new ExactModelProfileInferenceClientRequest(
            primary,
            identity,
            policy.Requirements.Budget.PerAttempt,
            policy.Requirements.Budget,
            baseline.RoutingAdmissionHash,
            baseline.AdmissionReceiptHash,
            baseline.AuthorityEvidenceHash,
            baseline.DataPostureEvidenceHash,
            baseline.ProviderAttemptId,
            baseline.ProviderCorrelationId);
    }

    private static CapabilityId ProfileId()
    {
        Assert.True(CapabilityId.TryParse(BuiltInCapabilityCatalog.CodexModelProfileCapabilityId, out var value, out _));
        return value!;
    }

    private static GovernedModelRoutingPolicy ExactPolicy(GovernedModelProfileMetadata metadata)
    {
        var unbounded = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Unbounded);
        var privacy = GovernedModelPrivacyRequirement.Create(
            1,
            localOnly: false,
            metadata.Privacy.Egress,
            metadata.Privacy.Destinations,
            metadata.Privacy.AcceptedDataClasses,
            metadata.Privacy.Regions,
            metadata.Privacy.Retention,
            metadata.Privacy.Training);
        return GovernedModelRoutingPolicy.Create(
            1,
            GovernedModelRoutingSelector.Exact(ProfileId()),
            [],
            GovernedModelProfileRequirements.Create(
                1,
                [GovernedModelModality.Text],
                [],
                1,
                1,
                privacy,
                GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded)));
    }

    private static ExactModelProfileInferenceClientRequest ResolverRequest()
    {
        var attempt = CanonicalInferenceAuthorityTestData.Request();
        var admission = Assert.IsType<GovernedLoopAdmissionReceipt>(attempt.AdmissionReceipt);
        var route = Assert.Single(admission.Evidence.ModelRoutingAdmission.Entries);
        var identity = GovernedModelUsageLedgerIdentity.Create(
            1,
            admission.Evidence.ModelRoutingAdmission.WorkspaceId,
            attempt.RunId,
            admission.Evidence.ModelRoutingAdmission.GraphId,
            admission.Evidence.ModelRoutingAdmission.GraphRevisionId,
            admission.Evidence.ModelRoutingAdmission.GraphExecutableHash,
            admission.Evidence.ModelRoutingAdmission.ExecutionGeneration,
            admission.ContentHash,
            admission.Evidence.ModelRoutingAdmission.ContentHash,
            new string('8', 64),
            new string('9', 64),
            route.NodeId,
            attempt.PlanOrdinal,
            attempt.ActivationOrdinal,
            attempt.VisitOrdinal,
            attempt.AttemptOperationId!,
            attempt.Attempt,
            route.Primary.ContentHash,
            route.Requirements.Budget.ContentHash);
        return new ExactModelProfileInferenceClientRequest(
            route.Primary,
            identity,
            route.Requirements.Budget.PerAttempt,
            route.Requirements.Budget,
            admission.Evidence.ModelRoutingAdmission.ContentHash,
            admission.ContentHash,
            new string('8', 64),
            new string('9', 64),
            attempt.AttemptOperationId!,
            identity.ContentHash);
    }

    private sealed class FixedDefaultSource(ModelProfileDefaultReadResult result) : IModelProfileDefaultSource
    {
        public Task<ModelProfileDefaultReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class FixedMetadataSource(ModelProfileSourceReadResult result) : IModelProfileMetadataSource
    {
        public Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId profileId, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class SelectingMetadataSource(CapabilityId ownedProfileId, ModelProfileSourceReadResult result) : IModelProfileMetadataSource
    {
        public Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(profileId.Equals(ownedProfileId)
                ? result
                : new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.NotFound, null, null));
        }
    }

    private sealed class FixedAdapterRegistry(ModelProfileAdapterPosture result) : IModelProfileAdapterRegistry
    {
        public Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class SelectingAdapterRegistry(string ownedMetadataHash, string registryRevisionHash) : IModelProfileAdapterRegistry
    {
        public Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ModelProfileAdapterPosture(
                string.Equals(metadata.ContentHash, ownedMetadataHash, StringComparison.Ordinal)
                    ? ModelProfileAdapterPostureStatus.Ready
                    : ModelProfileAdapterPostureStatus.Unregistered,
                metadata.ContentHash,
                registryRevisionHash));
        }
    }

    private sealed class FixedResolver : IExactModelProfileInferenceClientResolver
    {
        private readonly ExactModelProfileInferenceClientResolution _result;

        internal FixedResolver(ExactModelProfileInferenceClientResolutionStatus status)
        {
            _result = new ExactModelProfileInferenceClientResolution(status, null);
        }

        internal FixedResolver(IExactModelProfileInferenceClientLease lease)
        {
            _result = new ExactModelProfileInferenceClientResolution(ExactModelProfileInferenceClientResolutionStatus.Resolved, lease);
        }

        public Task<ExactModelProfileInferenceClientResolution> ResolveAsync(ExactModelProfileInferenceClientRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class RecordingLease(ExactModelProfileInferenceClientRequest request) : IExactModelProfileInferenceClientLease
    {
        public string ProfilePinHash => request.Primary.ContentHash;
        public string ConfigurationHash => request.Primary.Metadata.ConfigurationHash;
        public ExactModelProfileEnforcementAcknowledgement Enforcement { get; } = new(
            request.Primary.ContentHash,
            request.AttemptIdentity.ContentHash,
            request.Reservation.ContentHash,
            request.BudgetPolicy.ContentHash,
            request.RoutingAdmissionHash,
            request.AdmissionReceiptHash,
            request.AuthorityEvidenceHash,
            request.DataPostureEvidenceHash,
            request.Primary.Metadata.ProviderId,
            LlmInferenceSurface.OpenAiCodex,
            request.ProviderAttemptId,
            request.ProviderCorrelationId,
            new string('7', 64));
        public ILlmInferenceClient Client => null!;
        internal bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
