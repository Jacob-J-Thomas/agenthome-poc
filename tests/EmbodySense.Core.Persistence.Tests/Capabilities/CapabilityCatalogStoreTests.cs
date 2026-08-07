using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityCatalogStoreTests : IDisposable
{
    private readonly TestWorkspace _defaultTrustRoot = new();
    private readonly FileCapabilityCatalogTrustProvider _defaultTrustProvider;

    public CapabilityCatalogStoreTests()
    {
        _defaultTrustProvider = new FileCapabilityCatalogTrustProvider(_defaultTrustRoot.RootPath);
    }

    public void Dispose()
    {
        _defaultTrustRoot.Dispose();
    }

    [Fact]
    public void Default_store_composition_does_not_mutate_server_state_during_construction()
    {
        using var workspace = new TestWorkspace();
        Assert.NotNull(new CapabilityCatalogStore(new WorkspacePaths(workspace.RootPath)));
    }

    [Fact]
    public async Task Lifecycle_transitions_change_only_the_requested_axis_and_removal_retains_a_tombstone()
    {
        using var workspace = new TestWorkspace();
        var service = Service(workspace);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var revision = 0L;

        var declared = await service.DeclareAsync(descriptor, revision, "declare-capability");
        var initial = Assert.IsType<CapabilityCatalogEntry>(declared.Entry);
        revision = Assert.IsType<long>(declared.CatalogRevision);
        Assert.Equal(CapabilityDeclarationState.Declared, initial.Lifecycle.Declaration);
        Assert.Equal(CapabilityInstallationState.NotInstalled, initial.Lifecycle.Installation);
        Assert.Equal(CapabilityEnablementState.Disabled, initial.Lifecycle.Enablement);
        Assert.Equal(CapabilityTrustState.Unverified, initial.Lifecycle.Trust);

        revision = Revision(await service.InstallAsync(descriptor.Id, revision, "install-capability"));
        revision = Revision(await service.VerifyAsync(descriptor.Id, revision, "verify-capability"));
        revision = Revision(await service.EnableAsync(descriptor.Id, revision, "enable-capability"));
        revision = Revision(await service.MarkDegradedAsync(descriptor.Id, revision, "degrade-capability"));
        revision = Revision(await service.MarkUnavailableAsync(descriptor.Id, revision, "unavailable-capability"));
        revision = Revision(await service.MarkHealthyAsync(descriptor.Id, revision, "recover-capability"));
        revision = Revision(await service.RejectTrustAsync(descriptor.Id, revision, "reject-capability"));
        revision = Revision(await service.VerifyAsync(descriptor.Id, revision, "reverify-capability"));
        revision = Revision(await service.DeprecateAsync(descriptor.Id, revision, "deprecate-capability"));
        var removed = await service.RemoveAsync(descriptor.Id, revision, "remove-capability");

        var tombstone = Assert.IsType<CapabilityCatalogEntry>(removed.Entry);
        Assert.Equal(descriptor.Id, tombstone.Descriptor.Id);
        Assert.Equal(CapabilityDeclarationState.Withdrawn, tombstone.Lifecycle.Declaration);
        Assert.Equal(CapabilityInstallationState.NotInstalled, tombstone.Lifecycle.Installation);
        Assert.Equal(CapabilityEnablementState.Disabled, tombstone.Lifecycle.Enablement);
        Assert.Equal(CapabilityHealthState.Unavailable, tombstone.Lifecycle.Health);
        Assert.Equal(CapabilityRetirementState.Removed, tombstone.Lifecycle.Retirement);
        Assert.Equal(CapabilityTrustState.Verified, tombstone.Lifecycle.Trust);
        var resurrection = await service.InstallAsync(descriptor.Id, removed.CatalogRevision!.Value, "resurrect-capability");
        var redeclaration = await service.DeclareAsync(descriptor, removed.CatalogRevision.Value, "redeclare-capability");
        Assert.Equal(CapabilityCatalogMutationStatus.Invalid, resurrection.Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Invalid, redeclaration.Status);
    }

    [Fact]
    public async Task Duplicate_operation_replays_exactly_while_reuse_and_stale_revision_conflict()
    {
        using var workspace = new TestWorkspace();
        var service = Service(workspace);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var mutation = new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "same-operation", 0, descriptor.Id, descriptor);

        var applied = await Store(new WorkspacePaths(workspace.RootPath)).MutateAsync(mutation);
        var later = await service.InstallAsync(descriptor.Id, 1, "install-later");
        var replayed = await Store(new WorkspacePaths(workspace.RootPath)).MutateAsync(mutation);
        var reused = await service.InstallAsync(descriptor.Id, 2, "same-operation");
        var stale = await service.InstallAsync(descriptor.Id, 0, "stale-install");

        Assert.Equal(CapabilityCatalogMutationStatus.Applied, applied.Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, later.Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Replayed, replayed.Status);
        Assert.Equal(applied.CatalogRevision, replayed.CatalogRevision);
        Assert.Equal(CapabilityInstallationState.NotInstalled, replayed.Entry!.Lifecycle.Installation);
        Assert.Equal(CapabilityCatalogMutationStatus.Conflict, reused.Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Conflict, stale.Status);
    }

    [Fact]
    public async Task Concurrent_stores_serialize_the_same_expected_revision()
    {
        using var workspace = new TestWorkspace();
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var first = Store(new WorkspacePaths(workspace.RootPath));
        var second = Store(new WorkspacePaths(workspace.RootPath));
        var mutations = new[]
        {
            first.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "concurrent-one", 0, descriptor.Id, descriptor)),
            second.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "concurrent-two", 0, descriptor.Id, descriptor))
        };

        var results = await Task.WhenAll(mutations);

        Assert.Single(results, result => result.Status == CapabilityCatalogMutationStatus.Applied);
        Assert.Single(results, result => result.Status == CapabilityCatalogMutationStatus.Conflict);
        var read = await first.ReadAsync(null, 10);
        Assert.Single(read.Page!.Entries);
    }

    [Fact]
    public async Task Mutation_does_not_report_applied_before_the_catalog_durability_barrier()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var declared = await Store(paths).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-before-durability-barrier", 0, descriptor.Id, descriptor));
        var barrier = new BlockingCapabilityCatalogDurabilityBarrier();
        var store = new CapabilityCatalogStore(paths, _defaultTrustProvider, durabilityBarrier: barrier);

        var mutation = store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "install-behind-durability-barrier", Revision(declared), descriptor.Id, null));
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(mutation.IsCompleted);
        barrier.Release();
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, (await mutation).Status);
        Assert.True(barrier.CallCount >= 2);
    }

    [Fact]
    public async Task Fresh_catalog_directory_chain_is_committed_before_any_catalog_artifact_rename()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var barrier = new RecordingCapabilityCatalogDurabilityBarrier();
        var store = new CapabilityCatalogStore(paths, _defaultTrustProvider, durabilityBarrier: barrier);

        var mutation = await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-after-directory-durability", 0, descriptor.Id, descriptor));

        Assert.Equal(CapabilityCatalogMutationStatus.Applied, mutation.Status);
        Assert.Equal("directory:" + paths.AgentPath, barrier.Events[0]);
        Assert.Equal("directory:" + paths.CapabilityCatalogPath, barrier.Events[1]);
        Assert.StartsWith("rename:", barrier.Events[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fresh_catalog_directory_barrier_failure_returns_unavailable_before_artifact_writes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var barrier = new RecordingCapabilityCatalogDurabilityBarrier { FailDirectoryCreateAt = 2 };
        var store = new CapabilityCatalogStore(paths, _defaultTrustProvider, durabilityBarrier: barrier);

        var mutation = await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-with-directory-durability-failure", 0, descriptor.Id, descriptor));

        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, mutation.Status);
        Assert.DoesNotContain(barrier.Events, entry => entry.StartsWith("rename:", StringComparison.Ordinal));
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
    }

    [Fact]
    public async Task Windows_staging_directory_substitution_is_blocked_while_identity_handle_is_retained()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var barrier = new SubstitutingCapabilityCatalogDurabilityBarrier { AttemptBeforeMove = true };
        var store = new CapabilityCatalogStore(paths, _defaultTrustProvider, durabilityBarrier: barrier);

        var mutation = await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-with-staging-substitution", 0, descriptor.Id, descriptor));

        Assert.Equal(CapabilityCatalogMutationStatus.Applied, mutation.Status);
        Assert.False(barrier.BeforeMoveSubstitutionSucceeded);
        Assert.Equal(2, barrier.BlockedBeforeMoveAttempts);
    }

    [Fact]
    public async Task Windows_destination_substitution_after_move_fails_closed_on_physical_identity_mismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var barrier = new SubstitutingCapabilityCatalogDurabilityBarrier { SubstituteAfterMove = true };
        var store = new CapabilityCatalogStore(paths, _defaultTrustProvider, durabilityBarrier: barrier);

        var mutation = await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-with-destination-substitution", 0, descriptor.Id, descriptor));

        Assert.True(barrier.AfterMoveSubstitutionSucceeded);
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, mutation.Status);
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
    }

    [Fact]
    public async Task Durability_barrier_failure_returns_unavailable_instead_of_applied()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var declared = await Store(paths).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-before-durability-failure", 0, descriptor.Id, descriptor));
        var barrier = new BlockingCapabilityCatalogDurabilityBarrier { Failure = new IOException("Injected durability failure.") };
        var store = new CapabilityCatalogStore(paths, _defaultTrustProvider, durabilityBarrier: barrier);

        var mutation = store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "install-with-durability-failure", Revision(declared), descriptor.Id, null));
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(mutation.IsCompleted);
        barrier.Release();

        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, (await mutation).Status);
    }

    [Fact]
    public async Task Unix_fifo_catalog_artifact_is_rejected_without_blocking()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        Assert.True(CapabilityCatalogUnixFifo.TryCreate(paths.CapabilityCatalogDocumentPath));

        var read = await Store(paths).ReadAsync(null, 10).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, read.Status);
    }

    [Fact]
    public async Task Corrupt_primary_recovers_last_proved_state_read_only_across_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var service = Service(workspace);
        await service.DeclareAsync(descriptor, 0, "declare-before-corruption");
        await service.InstallAsync(descriptor.Id, 1, "install-before-corruption");
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, "{broken");

        var restarted = Store(paths);
        var recovered = await restarted.ReadAsync(null, 10);
        var rejected = await restarted.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "install-after-corruption", 1, descriptor.Id, null));

        Assert.Equal(CapabilityCatalogReadStatus.RecoveredLastProved, recovered.Status);
        var prior = Assert.Single(recovered.Page!.Entries);
        Assert.Equal(CapabilityInstallationState.NotInstalled, prior.Lifecycle.Installation);
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, rejected.Status);
        Assert.Equal("{broken", await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath));
    }

    [Fact]
    public async Task Unsupported_or_forged_artifacts_fail_closed_and_partial_temporary_files_are_ignored()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-safe", 0, descriptor.Id, descriptor));
        await File.WriteAllTextAsync(Path.Combine(paths.CapabilityCatalogPath, ".catalog.json.crash.tmp"), "partial-secret-canary");

        Assert.Equal(CapabilityCatalogReadStatus.Available, (await store.ReadAsync(null, 10)).Status);
        var unsupported = (await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath)).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, unsupported);
        Assert.Equal(CapabilityCatalogReadStatus.RecoveredLastProved, (await store.ReadAsync(null, 10)).Status);
        await File.WriteAllTextAsync(paths.CapabilityCatalogProofPath, unsupported.Replace("{", "{\"authority\":\"self-granted\",", StringComparison.Ordinal));

        var unavailable = await Store(paths).ReadAsync(null, 10);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Page);
    }

    [Fact]
    public async Task Rehashed_source_descriptor_cannot_self_assign_trust_authority_or_secret_values()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-before-forgery", 0, descriptor.Id, descriptor));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath))!.AsObject();
        var entry = root["entries"]!.AsArray()[0]!.AsObject();
        var sourceDescriptor = JsonNode.Parse(entry["descriptorJson"]!.GetValue<string>())!.AsObject();
        sourceDescriptor["trust"] = "verified";
        sourceDescriptor["authority"] = "ambient";
        sourceDescriptor["secretValue"] = "actual-secret-value-canary";
        entry["descriptorJson"] = sourceDescriptor.ToJsonString(JsonOptions(writeIndented: false));
        ApplyForgedUnkeyedAuthenticationTag(root);
        var forged = root.ToJsonString(JsonOptions(writeIndented: true));
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, forged);
        await File.WriteAllTextAsync(paths.CapabilityCatalogProofPath, forged);

        var read = await Store(paths).ReadAsync(null, 10);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, read.Status);
        Assert.Null(read.Page);
    }

    [Fact]
    public async Task Rehashed_lifecycle_state_cannot_self_verify_trust_even_when_both_public_artifacts_are_replaced()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-before-lifecycle-forgery", 0, descriptor.Id, descriptor));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath))!.AsObject();
        root["entries"]!.AsArray()[0]!.AsObject()["trust"] = "verified";
        ApplyForgedUnkeyedAuthenticationTag(root);
        var forged = root.ToJsonString(JsonOptions(writeIndented: true));
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, forged);
        await File.WriteAllTextAsync(paths.CapabilityCatalogProofPath, forged);

        var read = await Store(paths).ReadAsync(null, 10);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, read.Status);
        Assert.Null(read.Page);
    }

    [Fact]
    public async Task Trust_anchor_and_authentication_key_are_server_owned_outside_the_workspace_and_never_regenerated()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var store = new CapabilityCatalogStore(paths, provider);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-authenticated", 0, descriptor.Id, descriptor));

        var artifact = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        var key = await File.ReadAllBytesAsync(provider.AuthenticationKeyPath);
        var anchorPath = provider.GetAnchorPath(CapabilityCatalogWorkspaceIdentity.Create(paths.RootPath));
        var anchor = await File.ReadAllTextAsync(anchorPath);
        var anchorJson = JsonNode.Parse(anchor)!.AsObject();
        Assert.Equal(32, key.Length);
        Assert.False(provider.RootPath.StartsWith(paths.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(paths.AgentPath, "*authentication*", SearchOption.TopDirectoryOnly));
        Assert.Contains("\"authenticationTag\": \"hmac-sha256:", artifact);
        Assert.Contains("\"contentDigest\": \"sha256:", artifact);
        Assert.DoesNotContain(Convert.ToBase64String(key), artifact, StringComparison.Ordinal);
        Assert.Equal(1, anchorJson["currentGeneration"]!.GetValue<long>());
        Assert.Equal(0, anchorJson["previousGeneration"]!.GetValue<long>());
        Assert.StartsWith("sha256:", anchorJson["currentContentDigest"]!.GetValue<string>());
        Assert.StartsWith("sha256:", anchorJson["previousContentDigest"]!.GetValue<string>());

        File.Delete(anchorPath);
        var missingAnchor = await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10);
        var rejectedMutation = await new CapabilityCatalogStore(paths, provider).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "install-with-missing-anchor", 1, descriptor.Id, null));

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, missingAnchor.Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, rejectedMutation.Status);
        Assert.False(File.Exists(anchorPath));

        await File.WriteAllTextAsync(anchorPath, anchor.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10)).Status);

        await File.WriteAllTextAsync(anchorPath, anchor);
        File.Delete(provider.AuthenticationKeyPath);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10)).Status);
        Assert.False(File.Exists(provider.AuthenticationKeyPath));

        await File.WriteAllBytesAsync(provider.AuthenticationKeyPath, key[..^1]);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10)).Status);
        await File.WriteAllBytesAsync(provider.AuthenticationKeyPath, RandomNumberGenerator.GetBytes(32));
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10)).Status);
    }

    [Fact]
    public async Task Malformed_authentication_tags_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var service = Service(workspace);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        await service.DeclareAsync(descriptor, 0, "declare-before-malformed-tag");
        var original = JsonNode.Parse(await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath))!.AsObject();

        await AssertRejectedAsync(original, root => root["authenticationTag"] = "sha256:" + new string('0', 64));
        await AssertRejectedAsync(original, root => root["authenticationTag"] = "hmac-sha256:00");

        async Task AssertRejectedAsync(JsonObject source, Action<JsonObject> corrupt)
        {
            var candidate = source.DeepClone().AsObject();
            corrupt(candidate);
            var json = candidate.ToJsonString(JsonOptions(writeIndented: true));
            await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, json);
            await File.WriteAllTextAsync(paths.CapabilityCatalogProofPath, json);
            Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await Store(paths).ReadAsync(null, 10)).Status);
        }
    }

    [Fact]
    public async Task Older_legitimately_signed_pairs_never_become_current_after_disable_reject_or_remove()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var service = Service(paths, provider);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var revision = Revision(await service.DeclareAsync(descriptor, 0, "declare-rollback"));
        revision = Revision(await service.InstallAsync(descriptor.Id, revision, "install-rollback"));
        revision = Revision(await service.VerifyAsync(descriptor.Id, revision, "verify-rollback"));
        revision = Revision(await service.EnableAsync(descriptor.Id, revision, "enable-rollback"));
        var enabledPair = await CapturePairAsync(paths);

        revision = Revision(await service.DisableAsync(descriptor.Id, revision, "disable-rollback"));
        var disabledPair = await CapturePairAsync(paths);
        await RestorePairAsync(paths, enabledPair);
        var recoveredEnabled = await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10);
        Assert.Equal(CapabilityCatalogReadStatus.RecoveredLastProved, recoveredEnabled.Status);
        Assert.Equal(CapabilityEnablementState.Enabled, Assert.Single(recoveredEnabled.Page!.Entries).Lifecycle.Enablement);
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, (await Service(paths, provider).MarkHealthyAsync(descriptor.Id, revision, "mutate-recovered-enabled")).Status);
        await RestorePairAsync(paths, disabledPair);

        revision = Revision(await service.RejectTrustAsync(descriptor.Id, revision, "reject-rollback"));
        var rejectedPair = await CapturePairAsync(paths);
        await RestorePairAsync(paths, disabledPair);
        var recoveredVerified = await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10);
        Assert.Equal(CapabilityCatalogReadStatus.RecoveredLastProved, recoveredVerified.Status);
        Assert.Equal(CapabilityTrustState.Verified, Assert.Single(recoveredVerified.Page!.Entries).Lifecycle.Trust);
        await RestorePairAsync(paths, rejectedPair);

        _ = Revision(await service.RemoveAsync(descriptor.Id, revision, "remove-rollback"));
        var removedPair = await CapturePairAsync(paths);
        await RestorePairAsync(paths, rejectedPair);
        var recoveredPreTombstone = await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10);
        Assert.Equal(CapabilityCatalogReadStatus.RecoveredLastProved, recoveredPreTombstone.Status);
        Assert.Equal(CapabilityRetirementState.Active, Assert.Single(recoveredPreTombstone.Page!.Entries).Lifecycle.Retirement);
        await RestorePairAsync(paths, enabledPair);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10)).Status);
        await RestorePairAsync(paths, removedPair);
        Assert.Equal(CapabilityRetirementState.Removed, Assert.Single((await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10)).Page!.Entries).Lifecycle.Retirement);
    }

    [Fact]
    public async Task Attacker_selected_key_and_reauthenticated_artifacts_cannot_replace_server_trust()
    {
        using var workspace = new TestWorkspace();
        using var serverTrustRoot = new TestWorkspace();
        using var attackerTrustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var serverProvider = new FileCapabilityCatalogTrustProvider(serverTrustRoot.RootPath);
        var server = Service(paths, serverProvider);
        var revision = Revision(await server.DeclareAsync(descriptor, 0, "server-declare"));
        revision = Revision(await server.InstallAsync(descriptor.Id, revision, "server-install"));
        _ = Revision(await server.RemoveAsync(descriptor.Id, revision, "server-remove"));

        File.Delete(paths.CapabilityCatalogDocumentPath);
        File.Delete(paths.CapabilityCatalogProofPath);
        var attackerProvider = new FileCapabilityCatalogTrustProvider(attackerTrustRoot.RootPath);
        var attacker = Service(paths, attackerProvider);
        revision = Revision(await attacker.DeclareAsync(descriptor, 0, "attacker-declare"));
        revision = Revision(await attacker.InstallAsync(descriptor.Id, revision, "attacker-install"));
        _ = Revision(await attacker.VerifyAsync(descriptor.Id, revision, "attacker-verify"));

        var read = await new CapabilityCatalogStore(paths, serverProvider).ReadAsync(null, 10);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, read.Status);
        Assert.Null(read.Page);
        var serverKey = await File.ReadAllBytesAsync(serverProvider.AuthenticationKeyPath);
        var attackerKey = await File.ReadAllBytesAsync(attackerProvider.AuthenticationKeyPath);
        Assert.False(serverKey.SequenceEqual(attackerKey));
    }

    [Fact]
    public async Task Workspace_and_anchor_substitution_are_bound_to_canonical_workspace_identity()
    {
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        using var copiedWorkspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var firstPaths = new WorkspacePaths(firstWorkspace.RootPath);
        var secondPaths = new WorkspacePaths(secondWorkspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        await Service(firstPaths, provider).DeclareAsync(descriptor, 0, "declare-first-substitution");
        await Service(secondPaths, provider).DeclareAsync(descriptor, 0, "declare-second-substitution");
        var firstPair = await CapturePairAsync(firstPaths);
        await RestorePairAsync(secondPaths, firstPair);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(secondPaths, provider).ReadAsync(null, 10)).Status);

        var firstAnchor = provider.GetAnchorPath(CapabilityCatalogWorkspaceIdentity.Create(firstPaths.RootPath));
        var secondAnchor = provider.GetAnchorPath(CapabilityCatalogWorkspaceIdentity.Create(secondPaths.RootPath));
        await File.WriteAllTextAsync(secondAnchor, await File.ReadAllTextAsync(firstAnchor));
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(secondPaths, provider).ReadAsync(null, 10)).Status);

        var copiedPaths = new WorkspacePaths(copiedWorkspace.RootPath);
        Directory.CreateDirectory(copiedPaths.CapabilityCatalogPath);
        await RestorePairAsync(copiedPaths, firstPair);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await new CapabilityCatalogStore(copiedPaths, provider).ReadAsync(null, 10)).Status);
    }

    [Fact]
    public async Task Crash_after_initial_anchor_creation_can_restart_from_empty_but_anchor_advance_failure_recovers_prior_read_only()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var failing = new FailingCapabilityCatalogTrustProvider(provider) { FailAfterNextInitialization = true };
        var descriptor = CapabilityCatalogTestData.Descriptor();

        var interruptedInitialization = await new CapabilityCatalogStore(paths, failing).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-interrupted-initialization", 0, descriptor.Id, descriptor));
        var emptyRestart = await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10);

        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, interruptedInitialization.Status);
        Assert.Equal(CapabilityCatalogReadStatus.Available, emptyRestart.Status);
        Assert.Empty(emptyRestart.Page!.Entries);

        failing.FailAuthenticationGeneration = 1;
        var interruptedAfterProof = await new CapabilityCatalogStore(paths, failing).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-interrupted-after-proof", 0, descriptor.Id, descriptor));
        var proofOnlyRestart = await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10);

        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, interruptedAfterProof.Status);
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
        Assert.True(File.Exists(paths.CapabilityCatalogProofPath));
        Assert.Equal(CapabilityCatalogReadStatus.RecoveredLastProved, proofOnlyRestart.Status);
        Assert.Empty(proofOnlyRestart.Page!.Entries);

        using var advanceWorkspace = new TestWorkspace();
        using var advanceTrustRoot = new TestWorkspace();
        var advancePaths = new WorkspacePaths(advanceWorkspace.RootPath);
        var advanceProvider = new FileCapabilityCatalogTrustProvider(advanceTrustRoot.RootPath);
        var advanceFailing = new FailingCapabilityCatalogTrustProvider(advanceProvider);
        var declared = await Service(advancePaths, advanceProvider).DeclareAsync(descriptor, 0, "declare-before-interrupted-advance");
        advanceFailing.FailNextAdvance = true;
        var interruptedAdvance = await new CapabilityCatalogStore(advancePaths, advanceFailing).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "install-interrupted-advance", declared.CatalogRevision!.Value, descriptor.Id, null));
        var priorRestart = await new CapabilityCatalogStore(advancePaths, advanceProvider).ReadAsync(null, 10);

        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, interruptedAdvance.Status);
        Assert.Equal(CapabilityCatalogReadStatus.RecoveredLastProved, priorRestart.Status);
        Assert.Equal(CapabilityInstallationState.NotInstalled, Assert.Single(priorRestart.Page!.Entries).Lifecycle.Installation);
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, (await Service(advancePaths, advanceProvider).InstallAsync(descriptor.Id, declared.CatalogRevision.Value, "retry-after-interrupted-advance")).Status);
    }

    [Fact]
    public async Task Reparse_workspace_catalog_and_trust_root_chains_fail_closed()
    {
        using var actualWorkspace = new TestWorkspace();
        using var linkHolder = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var workspaceLink = linkHolder.File("workspace-link");
        if (!TryCreateDirectoryLink(workspaceLink, actualWorkspace.RootPath))
        {
            return;
        }

        var descriptor = CapabilityCatalogTestData.Descriptor();
        var linkedPaths = new WorkspacePaths(workspaceLink);
        var linkedResult = await new CapabilityCatalogStore(linkedPaths, new FileCapabilityCatalogTrustProvider(trustRoot.RootPath)).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-linked-workspace", 0, descriptor.Id, descriptor));
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, linkedResult.Status);

        using var catalogWorkspace = new TestWorkspace();
        using var catalogTarget = new TestWorkspace();
        using var catalogTrust = new TestWorkspace();
        var catalogPaths = new WorkspacePaths(catalogWorkspace.RootPath);
        Directory.CreateDirectory(catalogPaths.AgentPath);
        Assert.True(TryCreateDirectoryLink(catalogPaths.CapabilityCatalogPath, catalogTarget.RootPath));
        var catalogResult = await new CapabilityCatalogStore(catalogPaths, new FileCapabilityCatalogTrustProvider(catalogTrust.RootPath)).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-linked-catalog", 0, descriptor.Id, descriptor));
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, catalogResult.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(catalogTarget.RootPath));

        using var trustWorkspace = new TestWorkspace();
        using var trustLinkHolder = new TestWorkspace();
        using var trustTarget = new TestWorkspace();
        var trustLink = trustLinkHolder.File("trust-link");
        Assert.True(TryCreateDirectoryLink(trustLink, trustTarget.RootPath));
        var trustPaths = new WorkspacePaths(trustWorkspace.RootPath);
        var trustResult = await new CapabilityCatalogStore(trustPaths, new FileCapabilityCatalogTrustProvider(trustLink)).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-linked-trust", 0, descriptor.Id, descriptor));
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, trustResult.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(trustTarget.RootPath));
    }

    [Fact]
    public async Task Concurrent_catalog_directory_substitution_cannot_redirect_an_in_flight_commit()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var declared = await Service(paths, provider).DeclareAsync(descriptor, 0, "declare-before-concurrent-substitution");
        var blocking = new BlockingCapabilityCatalogTrustProvider(provider) { BlockNextRead = true };
        var store = new CapabilityCatalogStore(paths, blocking);
        var mutation = store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "install-during-concurrent-substitution", Revision(declared), descriptor.Id, null));
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        var retainedPath = workspace.File("retained-capabilities");
        var moved = false;
        var linked = false;
        try
        {
            Directory.Move(paths.CapabilityCatalogPath, retainedPath);
            moved = true;
            linked = TryCreateDirectoryLink(paths.CapabilityCatalogPath, outside.RootPath);
            if (!linked)
            {
                Directory.Move(retainedPath, paths.CapabilityCatalogPath);
                moved = false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.False(moved);
        }
        finally
        {
            blocking.Release();
        }

        var result = await mutation;
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.RootPath));
        if (moved)
        {
            Assert.True(linked);
            Directory.Delete(paths.CapabilityCatalogPath);
            Directory.Move(retainedPath, paths.CapabilityCatalogPath);
        }
        if (OperatingSystem.IsWindows())
        {
            Assert.False(moved);
        }
    }

    [Fact]
    public async Task Reparse_lock_key_anchor_primary_and_proof_files_fail_closed_without_following_targets()
    {
        foreach (var kind in new[] { "lock", "trust-lock", "key", "anchor", "primary", "proof" })
        {
            using var workspace = new TestWorkspace();
            using var trustRoot = new TestWorkspace();
            using var outside = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);
            var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
            var descriptor = CapabilityCatalogTestData.Descriptor();
            await Service(paths, provider).DeclareAsync(descriptor, 0, $"declare-before-{kind}-link");
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.Create(paths.RootPath);
            var unsafePath = kind switch
            {
                "lock" => paths.CapabilityCatalogLockPath,
                "trust-lock" => provider.TrustLockPath,
                "key" => provider.AuthenticationKeyPath,
                "anchor" => provider.GetAnchorPath(workspaceIdentity),
                "primary" => paths.CapabilityCatalogDocumentPath,
                _ => paths.CapabilityCatalogProofPath
            };
            File.Delete(unsafePath);
            var outsideTarget = outside.File($"missing-{kind}-target");
            if (!TryCreateFileLink(unsafePath, outsideTarget))
            {
                return;
            }

            var read = await new CapabilityCatalogStore(paths, provider).ReadAsync(null, 10);
            var mutation = await new CapabilityCatalogStore(paths, provider).MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, $"install-with-{kind}-link", 1, descriptor.Id, null));

            Assert.Equal(CapabilityCatalogReadStatus.Unavailable, read.Status);
            Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, mutation.Status);
            Assert.False(File.Exists(outsideTarget));
        }
    }

    [Fact]
    public async Task Workspaces_and_bounded_pages_are_isolated_and_deterministic()
    {
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        var first = Service(firstWorkspace);
        var second = Service(secondWorkspace);
        var firstDescriptor = CapabilityCatalogTestData.Descriptor("org.example/a");
        var secondDescriptor = CapabilityCatalogTestData.Descriptor("org.example/b");
        await first.DeclareAsync(secondDescriptor, 0, "declare-b");
        await first.DeclareAsync(firstDescriptor, 1, "declare-a");

        var firstPage = await first.ReadAsync(null, 1);
        var secondPage = await first.ReadAsync(firstPage.Page!.NextCursor, 1);
        var otherWorkspace = await second.ReadAsync(null, 10);

        Assert.Equal(firstDescriptor.Id, Assert.Single(firstPage.Page.Entries).Descriptor.Id);
        Assert.Equal(secondDescriptor.Id, Assert.Single(secondPage.Page!.Entries).Descriptor.Id);
        Assert.Empty(otherWorkspace.Page!.Entries);
        Assert.Equal(0, otherWorkspace.Page.CatalogRevision);
    }

    [Fact]
    public async Task Canonical_projection_contains_secret_references_but_no_secret_values_or_private_configuration()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var descriptor = CapabilityCatalogTestData.Descriptor(secretReference: "secret-reference-name");
        await Service(workspace).DeclareAsync(descriptor, 0, "declare-secret-reference");

        var json = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);

        Assert.Contains("secret-reference-name", json);
        Assert.DoesNotContain("actual-secret-value-canary", json);
        Assert.DoesNotContain("privateImplementationConfig", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assignment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_change_has_a_durable_receipt_without_advancing_state_revision()
    {
        using var workspace = new TestWorkspace();
        var service = Service(workspace);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var declared = await service.DeclareAsync(descriptor, 0, "declare-for-no-change");
        var noChange = await service.DisableAsync(descriptor.Id, declared.CatalogRevision!.Value, "disable-already-disabled");
        var replay = await service.DisableAsync(descriptor.Id, declared.CatalogRevision.Value, "disable-already-disabled");

        Assert.Equal(CapabilityCatalogMutationStatus.NoChange, noChange.Status);
        Assert.Equal(declared.CatalogRevision, noChange.CatalogRevision);
        Assert.Equal(CapabilityCatalogMutationStatus.Replayed, replay.Status);
        Assert.Equal(noChange.CatalogRevision, replay.CatalogRevision);
    }

    [Fact]
    public async Task Invalid_queries_mutations_and_unknown_targets_return_closed_structured_outcomes()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var descriptor = CapabilityCatalogTestData.Descriptor();
        _ = CapabilityId.TryParse("org.example/other", out var otherId, out _);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await store.ReadAsync(null, 0)).Status);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await store.ReadAsync("not-canonical", 10)).Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Invalid, (await store.MutateAsync(null!)).Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Invalid, (await store.MutateAsync(new CapabilityCatalogMutation(0, "operation", 0, descriptor.Id, descriptor))).Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Invalid, (await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "UPPERCASE", 0, descriptor.Id, descriptor))).Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Invalid, (await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "mismatched", 0, otherId, descriptor))).Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Invalid, (await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "descriptor-on-transition", 0, descriptor.Id, descriptor))).Status);
        Assert.Equal(CapabilityCatalogMutationStatus.NotFound, (await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Install, "unknown-target", 0, descriptor.Id, null))).Status);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_into_an_availability_outcome()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(null, 10, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "cancelled-operation", 0, descriptor.Id, descriptor), cancellation.Token));
    }

    [Fact]
    public async Task Contended_cross_process_lock_fails_closed_without_writing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        await using var heldLock = new FileStream(paths.CapabilityCatalogLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = Store(paths);
        var descriptor = CapabilityCatalogTestData.Descriptor();

        var readTask = store.ReadAsync(null, 10);
        var mutationTask = store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "blocked-operation", 0, descriptor.Id, descriptor));
        await Task.WhenAll(readTask, mutationTask);
        var read = await readTask;
        var mutation = await mutationTask;

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, read.Status);
        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, mutation.Status);
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
    }

    [Fact]
    public async Task Empty_and_oversized_primary_and_proof_artifacts_are_never_partially_trusted()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, string.Empty);
        await File.WriteAllTextAsync(paths.CapabilityCatalogProofPath, string.Empty);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await Store(paths).ReadAsync(null, 10)).Status);
        await using (var primary = new FileStream(paths.CapabilityCatalogDocumentPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var proof = new FileStream(paths.CapabilityCatalogProofPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            primary.SetLength(CapabilityCatalogLimits.MaximumArtifactUtf8Bytes + 1L);
            proof.SetLength(CapabilityCatalogLimits.MaximumArtifactUtf8Bytes + 1L);
        }

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, (await Store(paths).ReadAsync(null, 10)).Status);
    }

    [Fact]
    public async Task Proof_write_failure_aborts_before_primary_or_anchor_advancement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CapabilityCatalogProofPath);
        var store = Store(paths);
        var descriptor = CapabilityCatalogTestData.Descriptor();

        var result = await store.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "declare-with-proof-failure", 0, descriptor.Id, descriptor));
        var read = await store.ReadAsync(null, 10);

        Assert.Equal(CapabilityCatalogMutationStatus.Unavailable, result.Status);
        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, read.Status);
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
        Assert.True(Directory.Exists(paths.CapabilityCatalogProofPath));
        Assert.Empty(Directory.EnumerateFiles(paths.CapabilityCatalogPath, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    private CapabilityCatalogService Service(TestWorkspace workspace) => new(Store(new WorkspacePaths(workspace.RootPath)));

    private CapabilityCatalogStore Store(WorkspacePaths paths) => new(paths, _defaultTrustProvider);

    private static CapabilityCatalogService Service(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider) => new(new CapabilityCatalogStore(paths, trustProvider));

    private static async Task<(string Primary, string Proof)> CapturePairAsync(WorkspacePaths paths)
    {
        return (await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath), await File.ReadAllTextAsync(paths.CapabilityCatalogProofPath));
    }

    private static async Task RestorePairAsync(WorkspacePaths paths, (string Primary, string Proof) pair)
    {
        Directory.CreateDirectory(paths.CapabilityCatalogPath);
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, pair.Primary);
        await File.WriteAllTextAsync(paths.CapabilityCatalogProofPath, pair.Proof);
    }

    private static bool TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static long Revision(CapabilityCatalogMutationResult result)
    {
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, result.Status);
        return Assert.IsType<long>(result.CatalogRevision);
    }

    private static void ApplyForgedUnkeyedAuthenticationTag(JsonObject root)
    {
        root["authenticationTag"] = string.Empty;
        var unkeyedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions(writeIndented: false))));
        root["authenticationTag"] = "hmac-sha256:" + Convert.ToHexString(unkeyedDigest).ToLowerInvariant();
    }

    private static JsonSerializerOptions JsonOptions(bool writeIndented) => new(JsonSerializerDefaults.Web) { WriteIndented = writeIndented };
}
