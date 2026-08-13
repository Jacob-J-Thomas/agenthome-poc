using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityArtifactStoreTests
{
    private static readonly byte[] _versionOne = "version-one"u8.ToArray();
    private static readonly byte[] _versionTwo = "version-two"u8.ToArray();
    private static readonly DateTimeOffset _activationTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions _canonicalActivationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [Theory]
    [InlineData(CapabilityLifecycleOperationKind.Disable)]
    [InlineData(CapabilityLifecycleOperationKind.Remove)]
    [InlineData(CapabilityLifecycleOperationKind.Upgrade)]
    public async Task Resolved_executable_cannot_acquire_launch_authority_after_lifecycle_transition_commits(CapabilityLifecycleOperationKind kind)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        var initialStore = new CapabilityArtifactStore(paths, artifactTrust, new AlwaysTrustedArtifactVerifier(), authorityTransaction: authority);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await initialStore.StageAsync(first);
        await initialStore.StageAsync(second);
        var activation = await initialStore.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-before-launch-fence"));
        var baselineState = new CapabilityLifecycleState(first.Manifest.Descriptor, first.Manifest.Checksum, true, false, activation.Activation!.Revision, "activate-before-launch-fence", activation.Activation.ActivatedAtUtc);
        var baseline = new CapabilityLifecycleBaseline(baselineState, 1, activation.Activation.Revision);
        var baselineSource = new StubCapabilityLifecycleBaselineSource { Baseline = baseline };
        var lifecycle = new CapabilityLifecycleMutationStore(paths, new TestCapabilityLifecycleTrustProvider(), baselineSource, initialStore, authorityTransaction: authority);
        var coordinated = new CapabilityArtifactStore(paths, artifactTrust, new AlwaysTrustedArtifactVerifier(), lifecycleStore: lifecycle, authorityTransaction: authority);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var registrationRequest = new CapabilityLifecyclePreviewRequest("register-before-launch-fence", CapabilityLifecycleOperationKind.Upgrade, first.Manifest.Descriptor.Id, first.Manifest.Descriptor, first.Manifest.Checksum);
        var registration = await lifecycle.PreviewAsync(registrationRequest, baseline, await index.CaptureAsync());
        var registered = await lifecycle.MutateAsync(registration, baseline, await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, registered.Status);
        var request = new CapabilityLifecyclePreviewRequest("transition-before-launch-" + kind.ToString().ToLowerInvariant(), kind, first.Manifest.Descriptor.Id, kind == CapabilityLifecycleOperationKind.Upgrade ? second.Manifest.Descriptor : null, kind == CapabilityLifecycleOperationKind.Upgrade ? second.Manifest.Checksum : null);
        var preview = await lifecycle.PreviewAsync(request, baseline, await index.CaptureAsync());
        var before = await lifecycle.ReadAsync(first.Manifest.Descriptor.Id);
        var invocation = new CapabilityExecutableInvocation(first.Manifest, string.Empty, "{}", "stale-launch-" + kind.ToString().ToLowerInvariant(), before.State!.Revision);
        var resolution = await coordinated.ResolveAsync(invocation);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Available, resolution.Status);

        var transition = await lifecycle.MutateAsync(preview, baseline, await index.CaptureAsync());
        var launchFence = await resolution.Lease!.AcquireLaunchFenceAsync();

        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, transition.Status);
        Assert.Null(launchFence);
        await resolution.Lease.DisposeAsync();
    }

    [Fact]
    public async Task Activated_package_dependencies_are_discovered_from_authenticated_immutable_evidence()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        Assert.True(CapabilityId.TryParse("org.example/dependency", out var dependencyId, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        var dependencies = new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.CapabilityPackage, stage.Manifest.Descriptor.Id, [new CapabilityDependency(dependencyId!, range!)], [], new CapabilityDependencyArtifactMetadata(stage.Manifest.Checksum, null));
        stage = stage with { Manifest = stage.Manifest with { Dependencies = dependencies } };

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(stage)).Status);
        Assert.Equal(CapabilityLifecycleArtifactEvidenceStatus.Proved, (await store.VerifyAsync(stage.Manifest.Descriptor, stage.Manifest.Checksum)).Status);
        Assert.Equal(CapabilityLifecycleArtifactEvidenceStatus.NotFound, (await store.VerifyAsync(stage.Manifest.Descriptor with { Version = CapabilityArtifactStoreTestData.Manifest(_versionTwo, "2.0.0").Descriptor.Version }, stage.Manifest.Checksum)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-package"))).Status);
        var discovered = Assert.Single(await store.DiscoverAsync());

        Assert.Equal(stage.Manifest.Descriptor.Id.Value, discovered.CapabilityId);
        Assert.Equal(stage.Manifest.Checksum.Value, discovered.ArtifactDigest);
        Assert.True(CapabilityDependencyManifestHash.TryCompute(dependencies, out var expectedHash, out _));
        Assert.True(CapabilityDependencyManifestHash.TryCompute(discovered.Manifest, out var actualHash, out _));
        Assert.Equal(expectedHash, actualHash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Package_discovery_and_lifecycle_upgrade_share_one_lock_order_under_opposing_lock_barriers(bool discoveryHoldsInnerLockFirst)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        var artifacts = new CapabilityArtifactStore(paths, artifactTrust, new AlwaysTrustedArtifactVerifier(), authorityTransaction: authority);
        var first = WithPackageDependencies(CapabilityArtifactStoreTestData.Stage(_versionOne));
        var second = WithPackageDependencies(CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0"));
        await artifacts.StageAsync(first);
        await artifacts.StageAsync(second);
        var activation = await artifacts.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-before-package-race"));
        var baselineState = new CapabilityLifecycleState(first.Manifest.Descriptor, first.Manifest.Checksum, true, false, activation.Activation!.Revision, "activate-before-package-race", activation.Activation.ActivatedAtUtc);
        var baseline = new CapabilityLifecycleBaseline(baselineState, 1, activation.Activation.Revision);
        var lifecycleTrust = new TestCapabilityLifecycleTrustProvider();
        var lifecycle = new CapabilityLifecycleMutationStore(paths, lifecycleTrust, new StubCapabilityLifecycleBaselineSource { Baseline = baseline }, artifacts, authorityTransaction: authority);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var registrationRequest = new CapabilityLifecyclePreviewRequest("register-before-package-race", CapabilityLifecycleOperationKind.Upgrade, first.Manifest.Descriptor.Id, first.Manifest.Descriptor, first.Manifest.Checksum);
        var registration = await lifecycle.PreviewAsync(registrationRequest, baseline, await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(registration, baseline, await index.CaptureAsync())).Status);
        var upgradeRequest = new CapabilityLifecyclePreviewRequest("upgrade-during-package-discovery", CapabilityLifecycleOperationKind.Upgrade, first.Manifest.Descriptor.Id, second.Manifest.Descriptor, second.Manifest.Checksum);
        var upgrade = await lifecycle.PreviewAsync(upgradeRequest, baseline, await index.CaptureAsync());
        var discoveryProbe = new ProbingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var mutationProbe = new ProbingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var blockingArtifactTrust = new BlockingCapabilityArtifactStateTrustProvider(artifactTrust) { BlockNextActivationRead = discoveryHoldsInnerLockFirst };
        var blockingLifecycleTrust = new BlockingCapabilityCatalogTrustProvider(lifecycleTrust) { BlockNextRead = !discoveryHoldsInnerLockFirst };
        var runtimeLifecycle = new CapabilityLifecycleMutationStore(paths, blockingLifecycleTrust, new StubCapabilityLifecycleBaselineSource { Baseline = baseline }, artifacts, authorityTransaction: discoveryHoldsInnerLockFirst ? mutationProbe : authority);
        var coordinated = new CapabilityArtifactStore(paths, blockingArtifactTrust, new AlwaysTrustedArtifactVerifier(), lifecycleStore: runtimeLifecycle, authorityTransaction: discoveryHoldsInnerLockFirst ? authority : discoveryProbe);
        Task<IReadOnlyList<CapabilityPackageDependencyDiscovery>> discoveryTask;
        Task<CapabilityLifecycleMutationResult> mutationTask;
        if (discoveryHoldsInnerLockFirst)
        {
            discoveryTask = coordinated.DiscoverAsync();
            await blockingArtifactTrust.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            mutationTask = runtimeLifecycle.MutateAsync(upgrade, baseline, await index.CaptureAsync());
            await mutationProbe.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(mutationTask.IsCompleted);
            blockingArtifactTrust.Release();
        }
        else
        {
            mutationTask = runtimeLifecycle.MutateAsync(upgrade, baseline, await index.CaptureAsync());
            await blockingLifecycleTrust.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            discoveryTask = coordinated.DiscoverAsync();
            await discoveryProbe.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(discoveryTask.IsCompleted);
            blockingLifecycleTrust.Release();
        }

        await Task.WhenAll(discoveryTask, mutationTask).WaitAsync(TimeSpan.FromSeconds(5));
        var mutationResult = await mutationTask;
        var discoveryResult = await discoveryTask;

        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, mutationResult.Status);
        var discovered = Assert.Single(discoveryResult);
        Assert.Contains(discovered.ArtifactDigest, new[] { first.Manifest.Checksum.Value, second.Manifest.Checksum.Value });
    }

    [Fact]
    public async Task Verified_artifact_stages_activates_and_survives_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(stage)).Status);
        var activated = await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-v1"));
        var restarted = await Store(workspace, paths).ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, activated.Status);
        Assert.Equal(1, activated.Activation!.Revision);
        Assert.Equal(stage.Manifest.Checksum, restarted.Activation!.ArtifactDigest);
        Assert.Equal(await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath), await File.ReadAllTextAsync(paths.CapabilityArtifactActivationProofPath));
    }

    [Fact]
    public async Task Duplicate_stage_and_exact_activation_operation_are_idempotent()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.NoChange, (await store.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Replayed, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"))).Status);
    }

    [Fact]
    public async Task Operation_reuse_and_stale_revision_fail_without_replacing_current()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate"));

        var reused = await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate"));
        var stale = await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 0, "activate-v2"));
        var current = await store.ReadAsync(first.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, reused.Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Conflict, stale.Status);
        Assert.Equal(first.Manifest.Checksum, current.Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Full_idempotency_ledger_refuses_new_operations_without_evicting_old_bindings()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new MutableAuthenticatedArtifactStateTrustProvider();
        var store = new CapabilityArtifactStore(paths, trust, new AlwaysTrustedArtifactVerifier(), new FixedTimeProvider(_activationTimestamp));
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(first)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.StageAsync(second)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "operation-0"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "operation-1"))).Status);

        var publicFixture = CreateActivationFixture(trust, first.Manifest, second.Manifest, 2);
        Assert.Equal(publicFixture.Utf8Json, await File.ReadAllBytesAsync(paths.CapabilityArtifactActivationPath));
        Assert.Equal(publicFixture.Utf8Json, await File.ReadAllBytesAsync(paths.CapabilityArtifactActivationProofPath));

        var seededFixture = CreateActivationFixture(trust, first.Manifest, second.Manifest, 255);
        await Task.WhenAll(
            File.WriteAllBytesAsync(paths.CapabilityArtifactActivationPath, seededFixture.Utf8Json),
            File.WriteAllBytesAsync(paths.CapabilityArtifactActivationProofPath, seededFixture.Utf8Json));
        trust.SetCurrent(255, seededFixture.ContentDigest);

        var provedSeed = await new CapabilityArtifactStore(paths, trust, new AlwaysTrustedArtifactVerifier(), new FixedTimeProvider(_activationTimestamp)).ReadAsync(first.Manifest.Descriptor.Id);
        var acceptedAtMaximum = await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 255, "operation-255"));

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, provedSeed.Status);
        Assert.Equal(255, provedSeed.Activation!.Revision);
        Assert.Equal(first.Manifest.Checksum, provedSeed.Activation.ArtifactDigest);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, acceptedAtMaximum.Status);
        Assert.Equal(256, acceptedAtMaximum.Activation!.Revision);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 256, "operation-new"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Replayed, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "operation-0"))).Status);
    }

    [Fact]
    public async Task Rollback_restores_immediately_prior_proved_artifact_and_is_replayable()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1"));
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate-v2"));

        var rolledBack = await store.RollbackAsync(first.Manifest.Descriptor.Id, 2, "rollback-v1");
        var replayed = await store.RollbackAsync(first.Manifest.Descriptor.Id, 2, "rollback-v1");

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, rolledBack.Status);
        Assert.Equal(first.Manifest.Checksum, rolledBack.Activation!.ArtifactDigest);
        Assert.Equal(second.Manifest.Checksum, rolledBack.Activation.PriorArtifactDigest);
        Assert.Equal(CapabilityArtifactStoreStatus.Replayed, replayed.Status);
    }

    [Fact]
    public async Task Missing_prior_artifact_cannot_roll_back()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));

        var result = await store.RollbackAsync(stage.Manifest.Descriptor.Id, 1, "rollback");

        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, result.Status);
        Assert.Equal(stage.Manifest.Checksum, result.Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Tampered_bytes_cannot_stage_or_replace_current_activation()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var tampered = stage with { Content = new CapabilityArtifactContent("tampered"u8) };

        var result = await store.StageAsync(tampered);
        var current = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, result.Status);
        Assert.Equal(stage.Manifest.Checksum, current.Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Caller_supplied_verified_claim_cannot_bypass_server_owned_reverification()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace, verifier: new RejectingArtifactVerifier());
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne) with { Trust = new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "forged", "Forged.") };

        var result = await store.StageAsync(stage);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Corrupt_primary_recovers_last_proof_read_only_and_blocks_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1"));
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, "{ forged }");

        var recovered = await store.ReadAsync(first.Manifest.Descriptor.Id);
        var mutation = await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate-v2"));

        Assert.Equal(first.Manifest.Checksum, recovered.Activation!.ArtifactDigest);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, mutation.Status);
        Assert.Equal(first.Manifest.Checksum, (await store.ReadAsync(first.Manifest.Descriptor.Id)).Activation!.ArtifactDigest);
    }

    [Fact]
    public async Task Forged_self_digest_is_rejected_and_does_not_replace_proof()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var primary = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath))!.AsObject();
        primary["revision"] = 999;
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, primary.ToJsonString());

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(1, read.Activation!.Revision);
    }

    [Fact]
    public async Task Forged_primary_and_proof_with_recomputed_unkeyed_digest_fail_server_owned_authentication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var forged = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath))!.AsObject();
        forged["revision"] = 999;
        forged["authenticationTag"] = string.Empty;
        forged["contentDigest"] = string.Empty;
        var compact = forged.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        forged["contentDigest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compact))).ToLowerInvariant();
        var forgedJson = forged.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, forgedJson);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationProofPath, forgedJson);

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, read.Status);
        Assert.Null(read.Activation);
    }

    [Theory]
    [InlineData("case-alias")]
    [InlineData("duplicate")]
    public async Task Structurally_ambiguous_primary_recovers_the_authenticated_proof(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var canonical = await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath);
        var malformed = mutation == "case-alias"
            ? canonical.Replace("\"schemaVersion\":", "\"SchemaVersion\":", StringComparison.Ordinal)
            : canonical.Replace("\"revision\": 1,", "\"revision\": 1,\n  \"revision\": 1,", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, malformed);

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);

        Assert.Equal(CapabilityArtifactStoreStatus.Applied, read.Status);
        Assert.NotNull(read.Activation);
        Assert.Equal(1, read.Activation.Revision);
        Assert.True(stage.Manifest.Checksum.FixedTimeEquals(read.Activation.ArtifactDigest));
    }

    [Theory]
    [InlineData("case-alias")]
    [InlineData("duplicate")]
    public async Task Structurally_ambiguous_activation_documents_fail_closed(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));
        var canonical = await File.ReadAllTextAsync(paths.CapabilityArtifactActivationPath);
        var malformed = mutation == "case-alias"
            ? canonical.Replace("\"schemaVersion\":", "\"SchemaVersion\":", StringComparison.Ordinal)
            : canonical.Replace("\"revision\": 1,", "\"revision\": 1,\n  \"revision\": 1,", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, malformed);
        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationProofPath, malformed);

        var read = await store.ReadAsync(stage.Manifest.Descriptor.Id);
        var mutationResult = await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 1, "activate-next"));

        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, read.Status);
        Assert.Null(read.Activation);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, mutationResult.Status);
    }

    [Theory]
    [InlineData("case-alias")]
    [InlineData("duplicate")]
    public async Task Structurally_ambiguous_staged_evidence_cannot_activate(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        var digestName = stage.Manifest.Checksum.Value["sha256:".Length..];
        var evidencePath = Path.Combine(paths.CapabilityArtifactsPath, "staged", digestName, "artifact.evidence.json");
        var canonical = await File.ReadAllTextAsync(evidencePath);
        var malformed = mutation == "case-alias"
            ? canonical.Replace("\"capabilityId\":", "\"CapabilityId\":", StringComparison.Ordinal)
            : canonical.Replace("\"capabilityId\":", "\"capabilityId\": \"capability/test\",\n  \"capabilityId\":", StringComparison.Ordinal);
        await File.WriteAllTextAsync(evidencePath, malformed);

        var activation = await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));

        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, activation.Status);
        Assert.Null(activation.Activation);
    }

    [Fact]
    public async Task Partial_or_conflicting_staged_content_never_activates()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var digestName = stage.Manifest.Checksum.Value["sha256:".Length..];
        var root = Path.Combine(paths.CapabilityArtifactsPath, "staged", digestName);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "artifact.evidence.json"), "forged");

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"))).Status);
    }

    [Fact]
    public async Task Artifact_store_files_do_not_modify_catalog_document_or_lifecycle_axes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalogSentinel = "catalog-owned-state";
        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, catalogSentinel);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate"));

        Assert.Equal(catalogSentinel, await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath));
    }

    [Fact]
    public async Task Invalid_requests_and_conflicting_immutable_content_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var invalid = stage with { Manifest = stage.Manifest with { SchemaVersion = 2 } };

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.StageAsync(invalid)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, -1, "activate"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.RollbackAsync(stage.Manifest.Descriptor.Id, -1, "rollback")).Status);

        await store.StageAsync(stage);
        var digestName = stage.Manifest.Checksum.Value["sha256:".Length..];
        var contentPath = Path.Combine(paths.CapabilityArtifactsPath, "staged", digestName, stage.Manifest.EntryPoint);
        await File.WriteAllBytesAsync(contentPath, _versionTwo);

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await store.StageAsync(stage)).Status);
    }

    [Fact]
    public async Task Stale_and_unproved_rollback_requests_preserve_current_activation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1"));
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate-v2"));

        Assert.Equal(CapabilityArtifactStoreStatus.Conflict, (await store.RollbackAsync(first.Manifest.Descriptor.Id, 1, "stale-rollback")).Status);

        await File.WriteAllTextAsync(paths.CapabilityArtifactActivationPath, "{ forged }");
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await store.RollbackAsync(first.Manifest.Descriptor.Id, 2, "unproved-rollback")).Status);
    }

    [Fact]
    public async Task Resolved_execution_lease_retains_the_exact_proved_executable_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await store.StageAsync(stage);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-for-execution"));

        var resolution = await store.ResolveAsync(new CapabilityExecutableInvocation(stage.Manifest, "caller-controlled-root", "{}", "resolve-execution", 1));

        Assert.Equal(CapabilityExecutableAvailabilityStatus.Available, resolution.Status);
        var lease = Assert.IsAssignableFrom<ICapabilityExecutableArtifactLease>(resolution.Lease);
        var executablePath = lease.ExecutablePath;
        await using (lease)
        {
            Assert.Equal(stage.Manifest.Checksum, lease.ArtifactDigest);
            Assert.Equal(1, lease.ActivationRevision);
            Assert.DoesNotContain("caller-controlled-root", lease.ExecutablePath, StringComparison.Ordinal);
            var retained = new byte[_versionOne.Length];
            Assert.Equal(retained.Length, RandomAccess.Read(lease.ExecutableHandle, retained, 0));
            Assert.Equal(_versionOne, retained);
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(lease.ExecutablePath, _versionTwo));
            }
        }
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(executablePath, _versionOne);
            Assert.Equal(_versionOne, File.ReadAllBytes(executablePath));
        }
    }

    [Fact]
    public async Task Execution_resolution_rejects_stale_revision_and_digest_evidence()
    {
        using var workspace = new TestWorkspace();
        var store = Store(workspace);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await store.StageAsync(first);
        await store.StageAsync(second);
        await store.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-v1-for-resolution"));

        var stale = await store.ResolveAsync(new CapabilityExecutableInvocation(first.Manifest, string.Empty, "{}", "resolve-stale", 2));
        var wrongDigest = await store.ResolveAsync(new CapabilityExecutableInvocation(second.Manifest, string.Empty, "{}", "resolve-wrong-digest", 1));

        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, stale.Status);
        Assert.Null(stale.Lease);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, wrongDigest.Status);
        Assert.Null(wrongDigest.Lease);
    }

    [Fact]
    public async Task Registered_lifecycle_aggregate_closes_direct_activation_bypass_and_becomes_resolution_truth()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var initialStore = Store(workspace, paths);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await initialStore.StageAsync(first);
        var initialActivation = await initialStore.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-before-registration"));
        var baselineState = new CapabilityLifecycleState(first.Manifest.Descriptor, first.Manifest.Checksum, true, false, initialActivation.Activation!.Revision, "activate-before-registration", initialActivation.Activation.ActivatedAtUtc);
        var baseline = new CapabilityLifecycleBaseline(baselineState, 1, initialActivation.Activation.Revision);
        var baselineSource = new StubCapabilityLifecycleBaselineSource { Baseline = baseline };
        var lifecycleStore = new CapabilityLifecycleMutationStore(paths, new TestCapabilityLifecycleTrustProvider(), baselineSource, initialStore);
        var coordinated = new CapabilityArtifactStore(paths, new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath), new AlwaysTrustedArtifactVerifier(), lifecycleStore: lifecycleStore);
        var coordinatedStage = await coordinated.StageAsync(second);
        Assert.True(coordinatedStage.Status == CapabilityArtifactStoreStatus.Applied, coordinatedStage.Detail);
        Assert.Equal(first.Manifest.Checksum, (await coordinated.ReadAsync(first.Manifest.Descriptor.Id)).Activation!.ArtifactDigest);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var request = new CapabilityLifecyclePreviewRequest("coordinated-upgrade", CapabilityLifecycleOperationKind.Upgrade, first.Manifest.Descriptor.Id, second.Manifest.Descriptor, second.Manifest.Checksum);
        var preview = await lifecycleStore.PreviewAsync(request, baseline, await index.CaptureAsync());

        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await coordinated.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "bypass-upgrade"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Invalid, (await coordinated.RollbackAsync(first.Manifest.Descriptor.Id, 1, "bypass-rollback")).Status);
        var applied = await lifecycleStore.MutateAsync(preview, baseline, await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, applied.Status);
        Assert.Equal(second.Manifest.Checksum, (await coordinated.ReadAsync(first.Manifest.Descriptor.Id)).Activation!.ArtifactDigest);
        Assert.Empty(await coordinated.DiscoverAsync());
        var currentResolution = await coordinated.ResolveAsync(new CapabilityExecutableInvocation(second.Manifest, string.Empty, "{}", "resolve-current-lifecycle", applied.State!.Revision));
        var staleResolution = await coordinated.ResolveAsync(new CapabilityExecutableInvocation(first.Manifest, string.Empty, "{}", "resolve-stale-lifecycle", applied.State.Revision));
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Available, currentResolution.Status);
        await currentResolution.Lease!.DisposeAsync();
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, staleResolution.Status);

        var removalRequest = new CapabilityLifecyclePreviewRequest("coordinated-remove", CapabilityLifecycleOperationKind.Remove, first.Manifest.Descriptor.Id);
        var removal = await lifecycleStore.PreviewAsync(removalRequest, baseline, await index.CaptureAsync());
        var removed = await lifecycleStore.MutateAsync(removal, baseline, await index.CaptureAsync());
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, removed.Status);
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await coordinated.ReadAsync(first.Manifest.Descriptor.Id)).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, (await coordinated.ResolveAsync(new CapabilityExecutableInvocation(second.Manifest, string.Empty, "{}", "resolve-removed-lifecycle", removed.State!.Revision))).Status);
        Assert.Empty(await coordinated.DiscoverAsync());
    }

    [Fact]
    public async Task Lifecycle_upgrade_and_rollback_reject_deleted_staged_artifacts_until_exact_content_is_restored()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var artifacts = Store(workspace, paths);
        var first = CapabilityArtifactStoreTestData.Stage(_versionOne);
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await artifacts.StageAsync(first);
        await artifacts.StageAsync(second);
        var initialActivation = await artifacts.ActivateAsync(new CapabilityArtifactActivationRequest(first.Manifest, 0, "activate-before-lifecycle-reproof"));
        var baselineState = new CapabilityLifecycleState(first.Manifest.Descriptor, first.Manifest.Checksum, true, false, initialActivation.Activation!.Revision, "activate-before-lifecycle-reproof", initialActivation.Activation.ActivatedAtUtc);
        var baseline = new CapabilityLifecycleBaseline(baselineState, 1, initialActivation.Activation.Revision);
        var lifecycle = new CapabilityLifecycleMutationStore(paths, new TestCapabilityLifecycleTrustProvider(), new StubCapabilityLifecycleBaselineSource { Baseline = baseline }, artifacts);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        var upgradeRequest = new CapabilityLifecyclePreviewRequest("delete-before-upgrade", CapabilityLifecycleOperationKind.Upgrade, first.Manifest.Descriptor.Id, second.Manifest.Descriptor, second.Manifest.Checksum);
        var upgrade = await lifecycle.PreviewAsync(upgradeRequest, baseline, await index.CaptureAsync());
        var generationBeforeUpgrade = (await lifecycle.ReadAsync(first.Manifest.Descriptor.Id)).LifecycleRevision;
        File.Delete(StagedExecutablePath(paths, second.Manifest));

        Assert.Equal(CapabilityLifecycleMutationStatus.NotFound, (await lifecycle.MutateAsync(upgrade, baseline, await index.CaptureAsync())).Status);
        Assert.Equal(generationBeforeUpgrade, (await lifecycle.ReadAsync(first.Manifest.Descriptor.Id)).LifecycleRevision);
        Assert.True((await artifacts.StageAsync(second)).Status is CapabilityArtifactStoreStatus.Applied or CapabilityArtifactStoreStatus.NoChange);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(upgrade, baseline, await index.CaptureAsync())).Status);

        var rollbackRequest = new CapabilityLifecyclePreviewRequest("delete-before-rollback", CapabilityLifecycleOperationKind.Rollback, first.Manifest.Descriptor.Id);
        var rollback = await lifecycle.PreviewAsync(rollbackRequest, baseline, await index.CaptureAsync());
        var generationBeforeRollback = (await lifecycle.ReadAsync(first.Manifest.Descriptor.Id)).LifecycleRevision;
        File.Delete(StagedExecutablePath(paths, first.Manifest));
        Assert.Equal(CapabilityLifecycleMutationStatus.NotFound, (await lifecycle.MutateAsync(rollback, baseline, await index.CaptureAsync())).Status);
        Assert.Equal(generationBeforeRollback, (await lifecycle.ReadAsync(first.Manifest.Descriptor.Id)).LifecycleRevision);
        Assert.True((await artifacts.StageAsync(first)).Status is CapabilityArtifactStoreStatus.Applied or CapabilityArtifactStoreStatus.NoChange);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(rollback, baseline, await index.CaptureAsync())).Status);
        Assert.Equal(first.Manifest.Checksum, (await lifecycle.ReadAsync(first.Manifest.Descriptor.Id)).State!.ArtifactDigest);
    }

    [Fact]
    public async Task Lifecycle_projection_fails_closed_when_registered_state_is_unavailable_or_incomplete()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var initial = Store(workspace, paths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await initial.StageAsync(stage);
        var activation = await initial.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-for-fail-closed-lifecycle"));
        var lifecycle = new StubCapabilityLifecycleMutationStore { ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Unavailable, null, [], [], null, "unavailable") };
        var coordinated = new CapabilityArtifactStore(paths, new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath), new AlwaysTrustedArtifactVerifier(), lifecycleStore: lifecycle);
        var invocation = new CapabilityExecutableInvocation(stage.Manifest, string.Empty, "{}", "resolve-unavailable-lifecycle", 1);

        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await coordinated.ReadAsync(stage.Manifest.Descriptor.Id)).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, (await coordinated.ResolveAsync(invocation)).Status);
        await Assert.ThrowsAsync<IOException>(() => coordinated.DiscoverAsync());

        lifecycle.ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, null, [], [], 1, "incomplete");
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await coordinated.ReadAsync(stage.Manifest.Descriptor.Id)).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, (await coordinated.ResolveAsync(invocation with { OperationId = "resolve-incomplete-lifecycle" })).Status);
        await Assert.ThrowsAsync<IOException>(() => coordinated.DiscoverAsync());

        var recoveredState = new CapabilityLifecycleState(stage.Manifest.Descriptor, stage.Manifest.Checksum, true, false, 1, "recovered", activation.Activation!.ActivatedAtUtc);
        lifecycle.ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.RecoveredLastProved, recoveredState, [], [], 1, "recovered");
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await coordinated.ReadAsync(stage.Manifest.Descriptor.Id)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await coordinated.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 1, "reject-recovered-activation"))).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await coordinated.RollbackAsync(stage.Manifest.Descriptor.Id, 1, "reject-recovered-rollback")).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, (await coordinated.ResolveAsync(invocation with { OperationId = "resolve-recovered-lifecycle" })).Status);
        await Assert.ThrowsAsync<IOException>(() => coordinated.DiscoverAsync());

        lifecycle.ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.RecoveredLastProved, null, [], [], 0, "recovered-before-registration");
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await coordinated.ReadAsync(stage.Manifest.Descriptor.Id)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await coordinated.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 1, "reject-unproved-registration-activation"))).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, (await coordinated.ResolveAsync(invocation with { OperationId = "resolve-unproved-registration" })).Status);
    }

    [Fact]
    public async Task Lifecycle_artifact_evidence_rejects_invalid_targets_and_propagates_cancellation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(workspace, paths);
        var manifest = CapabilityArtifactStoreTestData.Manifest(_versionOne);

        Assert.Equal(CapabilityLifecycleArtifactEvidenceStatus.NotFound, (await store.VerifyAsync(manifest.Descriptor with { SchemaVersion = 2 }, manifest.Checksum)).Status);
        Assert.Equal(CapabilityLifecycleArtifactEvidenceStatus.Unavailable, (await store.VerifyAsync(manifest.Descriptor, manifest.Checksum)).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Incompatible, (await store.ResolveAsync(new CapabilityExecutableInvocation(manifest, string.Empty, "{}", "invalid-resolution-revision", 0))).Status);

        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.VerifyAsync(manifest.Descriptor, manifest.Checksum, cancellation.Token));
    }

    [Fact]
    public async Task Artifact_io_failures_are_structured_without_exposing_partial_activation()
    {
        using var malformedWorkspace = new TestWorkspace();
        var malformedPaths = new WorkspacePaths(malformedWorkspace.RootPath);
        Directory.CreateDirectory(malformedPaths.CapabilityCatalogPath);
        await File.WriteAllTextAsync(malformedPaths.CapabilityArtifactsPath, "not-a-directory");
        var malformed = Store(malformedWorkspace, malformedPaths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);

        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await malformed.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await malformed.ReadAsync(stage.Manifest.Descriptor.Id)).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, (await malformed.ResolveAsync(new CapabilityExecutableInvocation(stage.Manifest, string.Empty, "{}", "resolve-malformed-artifact-root", 1))).Status);

        using var activationWorkspace = new TestWorkspace();
        var activationPaths = new WorkspacePaths(activationWorkspace.RootPath);
        var activationTrust = new FileCapabilityArtifactStateTrustProvider(activationWorkspace.ServerStatePath);
        var activationNormal = new CapabilityArtifactStore(activationPaths, activationTrust, new AlwaysTrustedArtifactVerifier());
        await activationNormal.StageAsync(stage);
        var activationFailing = new CapabilityArtifactStore(activationPaths, activationTrust, new AlwaysTrustedArtifactVerifier(), durabilityBarrier: new FailingCapabilityLifecycleDurabilityBarrier { DestinationSuffix = "activation.json" });
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await activationFailing.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "fail-activation-commit"))).Status);

        using var rollbackWorkspace = new TestWorkspace();
        var rollbackPaths = new WorkspacePaths(rollbackWorkspace.RootPath);
        var rollbackTrust = new FileCapabilityArtifactStateTrustProvider(rollbackWorkspace.ServerStatePath);
        var rollbackNormal = new CapabilityArtifactStore(rollbackPaths, rollbackTrust, new AlwaysTrustedArtifactVerifier());
        var second = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        await rollbackNormal.StageAsync(stage);
        await rollbackNormal.StageAsync(second);
        await rollbackNormal.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-v1-before-failed-rollback"));
        await rollbackNormal.ActivateAsync(new CapabilityArtifactActivationRequest(second.Manifest, 1, "activate-v2-before-failed-rollback"));
        var rollbackFailing = new CapabilityArtifactStore(rollbackPaths, rollbackTrust, new AlwaysTrustedArtifactVerifier(), durabilityBarrier: new FailingCapabilityLifecycleDurabilityBarrier { DestinationSuffix = "activation.json" });
        Assert.Equal(CapabilityArtifactStoreStatus.Unavailable, (await rollbackFailing.RollbackAsync(stage.Manifest.Descriptor.Id, 2, "fail-rollback-commit")).Status);
    }

    [Fact]
    public async Task Staged_descriptor_and_content_must_remain_exactly_proved_at_activation()
    {
        using var descriptorWorkspace = new TestWorkspace();
        var descriptorPaths = new WorkspacePaths(descriptorWorkspace.RootPath);
        var descriptorStore = Store(descriptorWorkspace, descriptorPaths);
        var stage = CapabilityArtifactStoreTestData.Stage(_versionOne);
        await descriptorStore.StageAsync(stage);
        var digestName = stage.Manifest.Checksum.Value["sha256:".Length..];
        var evidencePath = Path.Combine(descriptorPaths.CapabilityArtifactsPath, "staged", digestName, "artifact.evidence.json");
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!.AsObject();
        evidence["descriptorJson"] = "{}";
        await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await descriptorStore.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "reject-forged-descriptor"))).Status);

        using var contentWorkspace = new TestWorkspace();
        var contentPaths = new WorkspacePaths(contentWorkspace.RootPath);
        var contentStore = Store(contentWorkspace, contentPaths);
        await contentStore.StageAsync(stage);
        File.Delete(Path.Combine(contentPaths.CapabilityArtifactsPath, "staged", digestName, stage.Manifest.EntryPoint));
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await contentStore.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "reject-missing-content"))).Status);

        using var trustWorkspace = new TestWorkspace();
        var trustPaths = new WorkspacePaths(trustWorkspace.RootPath);
        var trustStore = Store(trustWorkspace, trustPaths);
        await trustStore.StageAsync(stage);
        var rejectingTrust = new RejectingStagedEvidenceTrustProvider(new FileCapabilityArtifactStateTrustProvider(trustWorkspace.ServerStatePath));
        var rejectingStore = new CapabilityArtifactStore(trustPaths, rejectingTrust, new AlwaysTrustedArtifactVerifier());
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await rejectingStore.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "reject-foreign-evidence"))).Status);

        using var headerWorkspace = new TestWorkspace();
        var headerPaths = new WorkspacePaths(headerWorkspace.RootPath);
        var headerStore = Store(headerWorkspace, headerPaths);
        await headerStore.StageAsync(stage);
        var unstaged = CapabilityArtifactStoreTestData.Stage(_versionTwo, "2.0.0");
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await headerStore.ActivateAsync(new CapabilityArtifactActivationRequest(unstaged.Manifest, 0, "reject-unstaged-target"))).Status);
        var headerEvidencePath = Path.Combine(headerPaths.CapabilityArtifactsPath, "staged", digestName, "artifact.evidence.json");
        var headerEvidence = JsonNode.Parse(await File.ReadAllTextAsync(headerEvidencePath))!.AsObject();
        headerEvidence["capabilityVersion"] = "9.9.9";
        await File.WriteAllTextAsync(headerEvidencePath, headerEvidence.ToJsonString());
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await headerStore.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "reject-forged-header"))).Status);

        using var discoveryWorkspace = new TestWorkspace();
        var discoveryPaths = new WorkspacePaths(discoveryWorkspace.RootPath);
        var discoveryStore = Store(discoveryWorkspace, discoveryPaths);
        await discoveryStore.StageAsync(stage);
        await discoveryStore.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-before-evidence-forgery"));
        var discoveryEvidencePath = Path.Combine(discoveryPaths.CapabilityArtifactsPath, "staged", digestName, "artifact.evidence.json");
        var discoveryEvidence = JsonNode.Parse(await File.ReadAllTextAsync(discoveryEvidencePath))!.AsObject();
        discoveryEvidence["capabilityVersion"] = "9.9.9";
        await File.WriteAllTextAsync(discoveryEvidencePath, discoveryEvidence.ToJsonString());
        await Assert.ThrowsAsync<FormatException>(() => discoveryStore.DiscoverAsync());

        using var pinWorkspace = new TestWorkspace();
        var pinStore = Store(pinWorkspace);
        await pinStore.StageAsync(stage);
        await pinStore.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-for-pin-check"));
        var alteredManifest = stage.Manifest with { EntryPoint = "different.exe" };
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, (await pinStore.ResolveAsync(new CapabilityExecutableInvocation(alteredManifest, string.Empty, "{}", "reject-policy-pin", 1))).Status);
        Assert.True(CapabilityId.TryParse("org.example/unknown-artifact", out var unknownId, out _));
        Assert.Equal(CapabilityArtifactStoreStatus.NotFound, (await pinStore.ReadAsync(unknownId!)).Status);
    }

    private static CapabilityArtifactStore Store(TestWorkspace workspace, WorkspacePaths? paths = null, ICapabilityArtifactTrustVerifier? verifier = null) => new(paths ?? new WorkspacePaths(workspace.RootPath), new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath), verifier ?? new AlwaysTrustedArtifactVerifier());

    private static AuthenticatedActivationFixture CreateActivationFixture(
        MutableAuthenticatedArtifactStateTrustProvider trust,
        CapabilityArtifactManifest first,
        CapabilityArtifactManifest second,
        int operationCount)
    {
        var operations = Enumerable.Range(0, operationCount)
            .Select(revision =>
            {
                var manifest = revision % 2 == 0 ? first : second;
                return new CapabilityArtifactOperationFixture(
                    $"operation-{revision}",
                    "activate",
                    manifest.Descriptor.Id.Value,
                    CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(manifest).Value,
                    manifest.Checksum.Value,
                    revision,
                    revision + 1);
            })
            .ToArray();
        var current = operationCount == 0 ? null : (operationCount - 1) % 2 == 0 ? first : second;
        var prior = operationCount < 2 ? null : (operationCount - 2) % 2 == 0 ? first : second;
        CapabilityArtifactActivationEntryFixture[] entries = current is null
            ? []
            : [new(
                current.Descriptor.Id.Value,
                current.Checksum.Value,
                prior?.Checksum.Value,
                operationCount,
                _activationTimestamp)];
        var document = new CapabilityArtifactActivationFixtureDocument(1, operationCount, entries, operations, string.Empty, string.Empty);
        var canonicalContent = JsonSerializer.Serialize(document, _canonicalActivationJsonOptions);
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(canonicalContent)).Value;
        var authenticated = document with
        {
            ContentDigest = digest,
            AuthenticationTag = trust.CreateActivationTag(operationCount, digest)
        };
        return new AuthenticatedActivationFixture(
            digest,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(authenticated, _canonicalActivationJsonOptions) + Environment.NewLine));
    }

    private static CapabilityArtifactStageRequest WithPackageDependencies(CapabilityArtifactStageRequest stage)
    {
        Assert.True(CapabilityId.TryParse("org.example/dependency", out var dependencyId, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        var dependencies = new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.CapabilityPackage, stage.Manifest.Descriptor.Id, [new CapabilityDependency(dependencyId!, range!)], [], new CapabilityDependencyArtifactMetadata(stage.Manifest.Checksum, null));
        return stage with { Manifest = stage.Manifest with { Dependencies = dependencies } };
    }

    private static string StagedExecutablePath(WorkspacePaths paths, CapabilityArtifactManifest manifest) => Path.Combine(paths.CapabilityArtifactsPath, "staged", manifest.Checksum.Value["sha256:".Length..], manifest.EntryPoint);

    private sealed class AlwaysTrustedArtifactVerifier : ICapabilityArtifactTrustVerifier
    {
        public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default) => Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "test-server-policy", "Verified."));
    }

    private sealed class RejectingArtifactVerifier : ICapabilityArtifactTrustVerifier
    {
        public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default) => Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Rejected, "test-server-policy", "Rejected."));
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class MutableAuthenticatedArtifactStateTrustProvider : ICapabilityArtifactStateTrustProvider
    {
        private string? _workspaceIdentity;
        private CapabilityArtifactTrustState? _current;

        public Task<string> AuthenticateStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Tag(workspaceIdentity, 0, artifactDigest + "\n" + evidenceDigest));
        }

        public Task<bool> VerifyStagedEvidenceAsync(string workspaceIdentity, string artifactDigest, string evidenceDigest, string authenticationTag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(string.Equals(authenticationTag, Tag(workspaceIdentity, 0, artifactDigest + "\n" + evidenceDigest), StringComparison.Ordinal));
        }

        public Task<CapabilityArtifactTrustState?> ReadActivationAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _workspaceIdentity ??= workspaceIdentity;
            Assert.Equal(_workspaceIdentity, workspaceIdentity);
            return Task.FromResult(_current);
        }

        public Task<CapabilityArtifactTrustState> InitializeActivationAsync(string workspaceIdentity, string contentDigest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _workspaceIdentity ??= workspaceIdentity;
            Assert.Equal(_workspaceIdentity, workspaceIdentity);
            _current ??= new CapabilityArtifactTrustState(0, contentDigest, null, null);
            return Task.FromResult(_current);
        }

        public Task<string> AuthenticateActivationAsync(string workspaceIdentity, long revision, string contentDigest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _workspaceIdentity ??= workspaceIdentity;
            Assert.Equal(_workspaceIdentity, workspaceIdentity);
            return Task.FromResult(CreateActivationTag(revision, contentDigest));
        }

        public Task<bool> VerifyActivationAsync(string workspaceIdentity, long revision, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _workspaceIdentity ??= workspaceIdentity;
            Assert.Equal(_workspaceIdentity, workspaceIdentity);
            return Task.FromResult(string.Equals(authenticationTag, CreateActivationTag(revision, contentDigest), StringComparison.Ordinal));
        }

        public Task<CapabilityArtifactTrustState> AdvanceActivationAsync(string workspaceIdentity, long expectedRevision, string expectedContentDigest, long newRevision, string newContentDigest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _workspaceIdentity ??= workspaceIdentity;
            Assert.Equal(_workspaceIdentity, workspaceIdentity);
            if (_current is null || _current.CurrentRevision != expectedRevision || !string.Equals(_current.CurrentContentDigest, expectedContentDigest, StringComparison.Ordinal) || newRevision != expectedRevision + 1)
            {
                throw new IOException("Test activation trust compare-exchange conflict.");
            }

            _current = new CapabilityArtifactTrustState(newRevision, newContentDigest, expectedRevision, expectedContentDigest);
            return Task.FromResult(_current);
        }

        internal string CreateActivationTag(long revision, string contentDigest)
        {
            Assert.NotNull(_workspaceIdentity);
            return Tag(_workspaceIdentity!, revision, contentDigest);
        }

        internal void SetCurrent(long revision, string contentDigest)
        {
            Assert.NotNull(_workspaceIdentity);
            _current = new CapabilityArtifactTrustState(revision, contentDigest, null, null);
        }

        private static string Tag(string workspaceIdentity, long revision, string contentDigest)
            => "test:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceIdentity}\n{revision}\n{contentDigest}"))).ToLowerInvariant();
    }

    private sealed record CapabilityArtifactActivationFixtureDocument(
        int SchemaVersion,
        long Revision,
        IReadOnlyList<CapabilityArtifactActivationEntryFixture> Entries,
        IReadOnlyList<CapabilityArtifactOperationFixture> Operations,
        string ContentDigest,
        string AuthenticationTag);

    private sealed record CapabilityArtifactActivationEntryFixture(
        string CapabilityId,
        string ArtifactDigest,
        string? PriorArtifactDigest,
        long Revision,
        DateTimeOffset ActivatedAtUtc);

    private sealed record CapabilityArtifactOperationFixture(
        string OperationId,
        string Kind,
        string CapabilityId,
        string RequestDigest,
        string ArtifactDigest,
        long ExpectedRevision,
        long ResultRevision);

    private sealed record AuthenticatedActivationFixture(string ContentDigest, byte[] Utf8Json);
}
