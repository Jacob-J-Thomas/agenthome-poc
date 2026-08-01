using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Persistence.Credentials.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

public sealed class CredentialRegistryStoreTests
{
    [Fact]
    public async Task Restart_readback_preserves_safe_state_evidence_and_tombstone()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new FixedTimeProvider());
        var registered = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, registered.Status);
        Assert.Equal(1, registered.RegistryRevision);

        var binding = Binding();
        var evidence = Evidence(binding);
        Assert.True((await store.AppendAsync(evidence, default)).Succeeded);
        var tombstone = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("tombstone-1"), 2, ReferenceId(), null, null, null, null, null));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, tombstone.Status);

        var restarted = await Store(paths).ReadAsync();
        Assert.True(restarted.Succeeded);
        Assert.Equal(3, restarted.RegistryRevision);
        Assert.Empty(restarted.Entries);
        var savedTombstone = Assert.Single(restarted.Tombstones);
        Assert.Equal("credential-1", savedTombstone.ReferenceId.Value);
        Assert.Equal("tombstone-1", savedTombstone.OperationId.Value);
        Assert.Equal(["register-1", "evidence-1", "tombstone-1"], restarted.Operations.Select(item => item.OperationId.Value));
        Assert.Equal("evidence-1", Assert.Single(restarted.Evidence).EvidenceId.Value);
    }

    [Fact]
    public async Task Retry_and_stale_or_changed_operation_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var first = await store.MutateAsync(Register(0));
        var replay = await store.MutateAsync(Register(0));
        var stale = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));
        var changed = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("register-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));

        Assert.Equal(CredentialRegistryMutationStatus.Applied, first.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Replayed, replay.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, stale.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, changed.Status);
    }

    [Fact]
    public async Task Partial_primary_recovers_only_from_last_proved_pair_and_workspace_substitution_fails()
    {
        using var source = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var sourceStore = Store(sourcePaths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await sourceStore.MutateAsync(Register(0))).Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await sourceStore.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);

        await File.WriteAllTextAsync(sourcePaths.CredentialRegistryPrivateDocumentPath, "{");
        var recovered = await Store(sourcePaths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(1, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);

        using var destination = new TestWorkspace();
        var destinationPaths = new WorkspacePaths(destination.RootPath);
        Directory.CreateDirectory(destinationPaths.CredentialRegistryPath);
        Directory.CreateDirectory(destinationPaths.CredentialRegistryPrivatePath);
        File.Copy(sourcePaths.CredentialRegistryProofPath, destinationPaths.CredentialRegistryDocumentPath);
        File.Copy(sourcePaths.CredentialRegistryPrivateProofPath, destinationPaths.CredentialRegistryPrivateDocumentPath);
        var substituted = await Store(destinationPaths).ReadAsync();
        Assert.False(substituted.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, substituted.Failure!.Code);
    }

    [Fact]
    public async Task Public_artifacts_never_contain_locator_or_submitted_secret_canaries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var publicText = await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath);
        var privateText = await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath);
        Assert.DoesNotContain(Locator().Value, publicText, StringComparison.Ordinal);
        Assert.Contains(Locator().Value, privateText, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-secret-canary", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext-envelope-canary", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("key-material-canary", publicText, StringComparison.Ordinal);

        var unsafeLocator = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id("unsafe-1"), 1, ReferenceId(), Reference(), Binding(), Id("consent-1"), CredentialProviderHealthStatus.Available, null);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, (await store.MutateAsync(unsafeLocator)).Status);
    }

    [Fact]
    public async Task Concurrent_optimistic_mutations_admit_exactly_one_writer()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store(paths).MutateAsync(Register(0))));
        Assert.Equal(1, attempts.Count(item => item.Status == CredentialRegistryMutationStatus.Applied));
        Assert.Equal(7, attempts.Count(item => item.Status is CredentialRegistryMutationStatus.Conflict or CredentialRegistryMutationStatus.Replayed));
        Assert.Equal(1, (await Store(paths).ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task Unsupported_or_fully_corrupt_artifacts_fail_closed_without_plaintext_fallback()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        await File.WriteAllTextAsync(paths.CredentialRegistryDocumentPath, "{\"schemaVersion\":2}");
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateDocumentPath, "{\"schemaVersion\":2}");
        await File.WriteAllTextAsync(paths.CredentialRegistryProofPath, "plaintext-secret-canary");
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateProofPath, "ciphertext-envelope-canary");

        var read = await Store(paths).ReadAsync();
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        var mutation = await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, mutation.Status);
    }

    [Fact]
    public async Task Evidence_is_bound_to_a_live_exact_registered_reference()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var binding = Binding();
        Assert.False((await store.AppendAsync(Evidence(binding), default)).Succeeded);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        Assert.True((await store.AppendAsync(Evidence(binding), default)).Succeeded);
    }

    [Fact]
    public async Task Evidence_scope_must_be_equal_to_or_narrower_than_the_registered_binding()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var binding = Binding();
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        var broaderScopes = new[]
        {
            binding.Scope with { WorkspaceId = "workspace-2" },
            binding.Scope with { RoleId = null, LoopId = null, LoopRevision = null, NodeId = null },
            binding.Scope with { Target = null },
            binding.Scope with { Capability = null, Implementation = null }
        };
        for (var index = 0; index < broaderScopes.Length; index++)
        {
            var rejected = await store.AppendAsync(Evidence(binding, $"broader-{index}", broaderScopes[index]), default);
            Assert.False(rejected.Succeeded);
            Assert.Equal(CredentialFailureCode.Unauthorized, rejected.Failure!.Code);
        }

        var narrower = binding.Scope with { NotBeforeUtc = new DateTimeOffset(2026, 8, 1, 11, 0, 0, TimeSpan.Zero), NotAfterUtc = new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.Zero) };
        Assert.True((await store.AppendAsync(Evidence(binding, "narrower-1", narrower), default)).Succeeded);
        var current = await store.ReadAsync();
        Assert.Equal(2, current.RegistryRevision);
        Assert.Equal("narrower-1", Assert.Single(current.Evidence).EvidenceId.Value);
    }

    [Fact]
    public async Task Shape_correct_locator_is_rejected_without_explicit_provider_ownership_verification()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var result = await new CredentialRegistryStore(paths).MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, result.Status);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Fact]
    public async Task Candidate_durability_failure_recovers_only_the_previously_proved_snapshot()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var barrier = new FailOnDurabilityCallBarrier(8);
        var store = new CredentialRegistryStore(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), new AcceptingLocatorVerifier(), durabilityBarrier: barrier);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        var failed = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, failed.Status);

        var recovered = await Store(paths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(1, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, (await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-2"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);
    }

    [Fact]
    public async Task Trust_anchor_advance_failure_never_acknowledges_an_untrusted_successor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FailingCapabilityCatalogTrustProvider(FileCapabilityCatalogTrustProvider.CreateDefault());
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        trust.FailNextAdvance = true;

        var failed = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, failed.Status);
        var recovered = await Store(paths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(1, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);
    }

    [Fact]
    public async Task External_lock_contention_honors_cancellation_without_changing_the_registry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        using var externalLock = new FileStream(paths.CredentialRegistryLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var blocked = await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null), cancellation.Token);

        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, blocked.Status);
        externalLock.Dispose();
        var current = await store.ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(1, current.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(current.Entries).Health);
    }

    [Fact]
    public async Task Same_physical_workspace_rollback_is_rejected_by_the_monotonic_trust_anchor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var oldPublic = await File.ReadAllBytesAsync(paths.CredentialRegistryDocumentPath);
        var oldPrivate = await File.ReadAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);
        Assert.True((await store.AppendAsync(Evidence(Binding()), default)).Succeeded);

        await File.WriteAllBytesAsync(paths.CredentialRegistryDocumentPath, oldPublic);
        await File.WriteAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath, oldPrivate);
        await File.WriteAllBytesAsync(paths.CredentialRegistryProofPath, oldPublic);
        await File.WriteAllBytesAsync(paths.CredentialRegistryPrivateProofPath, oldPrivate);

        var read = await Store(paths).ReadAsync();
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
    }

    [Fact]
    public async Task Matching_operation_replay_retains_the_immutable_original_receipt_after_later_tombstone()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var original = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, original.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("tombstone-1"), 2, ReferenceId(), null, null, null, null, null))).Status);

        var replay = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Replayed, replay.Status);
        Assert.Equal(1, replay.RegistryRevision);
        Assert.NotNull(replay.Entry);
        Assert.Equal(CredentialProviderHealthStatus.Available, replay.Entry!.Health);
        Assert.Equal(1, replay.Entry.Revision);
    }

    [Fact]
    public async Task Cancellation_while_trust_is_unavailable_does_not_acknowledge_or_poison_a_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new BlockingCapabilityCatalogTrustProvider(FileCapabilityCatalogTrustProvider.CreateDefault());
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        trust.BlockNextRead = true;
        using var cancellation = new CancellationTokenSource();
        var pending = store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null), cancellation.Token);
        await trust.Entered;
        cancellation.Cancel();
        var cancelled = await pending;
        trust.Release();

        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, cancelled.Status);
        var current = await Store(paths).ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(1, current.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(current.Entries).Health);
    }

    [Fact]
    public async Task Replaced_registry_directory_reparse_point_is_rejected_without_writing_outside_the_workspace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        Directory.Delete(paths.CredentialRegistryPath, recursive: true);
        Directory.CreateSymbolicLink(paths.CredentialRegistryPath, outside.RootPath);

        var read = await Store(paths).ReadAsync();
        var mutation = await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null));
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, mutation.Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Submitted_locator_canary_crosses_only_the_verifier_and_private_artifact_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var verifier = new RecordingLocatorVerifier();
        var store = new CredentialRegistryStore(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), verifier);
        var locatorCanary = Locator("loc_c0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0de");

        var registered = await store.MutateAsync(Register(1, 0, locatorCanary));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, registered.Status);
        Assert.Equal(locatorCanary.Value, Assert.Single(verifier.Locators));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-canary"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);

        var publicArtifacts = new[] { await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), await File.ReadAllTextAsync(paths.CredentialRegistryProofPath) };
        var privateArtifacts = new[] { await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath), await File.ReadAllTextAsync(paths.CredentialRegistryPrivateProofPath) };
        Assert.All(publicArtifacts, artifact => Assert.DoesNotContain(locatorCanary.Value, artifact, StringComparison.Ordinal));
        Assert.All(privateArtifacts, artifact => Assert.Contains(locatorCanary.Value, artifact, StringComparison.Ordinal));
        Assert.DoesNotContain(locatorCanary.Value, JsonSerializer.Serialize(registered), StringComparison.Ordinal);
        Assert.DoesNotContain(locatorCanary.Value, JsonSerializer.Serialize(await store.ReadAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_quota_is_preflighted_without_recording_the_rejected_operation()
    {
        using var workspace = new TestWorkspace();
        var quota = new CredentialRegistryQuota(2, 2, 4, 4, 128 * 1024);
        var store = new CredentialRegistryStore(new WorkspacePaths(workspace.RootPath), FileCapabilityCatalogTrustProvider.CreateDefault(), new AcceptingLocatorVerifier(), quota: quota);
        for (var index = 0; index < quota.MaximumEntries; index++)
        {
            Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(index, index))).Status);
        }

        var rejected = await store.MutateAsync(Register(quota.MaximumEntries, quota.MaximumEntries));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, rejected.Status);
        Assert.Equal(CredentialFailureCode.LimitExceeded, rejected.Failure!.Code);
        var current = await store.ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(quota.MaximumEntries, current.Entries.Count);
        Assert.Equal(quota.MaximumEntries, current.Operations.Count);
        Assert.DoesNotContain(current.Operations, operation => operation.OperationId.Value == $"register-{quota.MaximumEntries}");
    }

    [Fact]
    public async Task Artifact_byte_quota_is_preflighted_before_any_registry_artifact_is_written()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new LongAuthenticationTagTrustProvider(FileCapabilityCatalogTrustProvider.CreateDefault(), 2048);
        var quota = new CredentialRegistryQuota(2, 2, 4, 4, 4096);
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier(), quota: quota);

        var rejected = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, rejected.Status);
        Assert.Equal(0, trust.InitializeCount);
        Assert.Equal(0, trust.AuthenticateCount);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryProofPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateProofPath));

        var valid = await Store(paths).MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, valid.Status);
        Assert.True((await Store(paths).ReadAsync()).Succeeded);
    }

    [Fact]
    public void Constructor_rejects_invalid_trust_and_quota_bounds()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CredentialRegistryStore(paths, new LongAuthenticationTagTrustProvider(FileCapabilityCatalogTrustProvider.CreateDefault(), 0), new AcceptingLocatorVerifier()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CredentialRegistryStore(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), new AcceptingLocatorVerifier(), quota: new CredentialRegistryQuota(0, 1, 1, 1, 1)));
    }

    [Fact]
    public async Task Invalid_mutation_shapes_fail_before_storage_access()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var invalid = new[]
        {
            await store.MutateAsync(null!),
            await store.MutateAsync(new CredentialRegistryMutation((CredentialRegistryMutationKind)999, Id("invalid-kind"), 0, ReferenceId(), null, null, null, null, null)),
            await store.MutateAsync(Register(0) with { ReferenceId = ReferenceId(2) }),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("invalid-health"), 0, ReferenceId(), Reference(), null, null, CredentialProviderHealthStatus.Available, null)),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("invalid-tombstone"), 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null))
        };

        Assert.All(invalid, result =>
        {
            Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
            Assert.Equal(CredentialFailureCode.InvalidRequest, result.Failure!.Code);
        });
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_returned_authentication_tags_fail_without_registry_artifacts(bool oversized)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var tag = oversized ? new string('a', 65) : string.Empty;
        var trust = new InvalidAuthenticationTagTrustProvider(FileCapabilityCatalogTrustProvider.CreateDefault(), tag);
        var result = await new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier()).MutateAsync(Register(0));

        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, result.Status);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Fact]
    public async Task Lookup_and_evidence_replay_conflict_and_quota_results_are_explicit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new CredentialRegistryQuota(2, 2, 4, 1, 128 * 1024);
        var store = new CredentialRegistryStore(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), new AcceptingLocatorVerifier(), quota: quota);
        var missing = await store.GetAsync(ReferenceId(), default);
        Assert.False(missing.Succeeded);
        Assert.Equal(CredentialFailureCode.NotFound, missing.Failure!.Code);

        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var found = await store.GetAsync(ReferenceId(), default);
        Assert.True(found.Succeeded);
        Assert.Equal(ReferenceId(), found.Reference!.Id);

        var binding = Binding();
        var invalidEvidence = Evidence(binding, "invalid-evidence") with { ReferenceId = null! };
        Assert.Equal(CredentialFailureCode.InvalidRequest, (await store.AppendAsync(invalidEvidence, default)).Failure!.Code);

        var evidence = Evidence(binding);
        Assert.True((await store.AppendAsync(evidence, default)).Succeeded);
        Assert.True((await store.AppendAsync(evidence, default)).Succeeded);
        var changedReplay = evidence with { UsedAtUtc = evidence.UsedAtUtc.AddMinutes(1) };
        Assert.Equal(CredentialFailureCode.Conflict, (await store.AppendAsync(changedReplay, default)).Failure!.Code);
        var wrongBinding = Evidence(binding, "wrong-binding") with { BindingHash = CredentialContractHash.Compute("forged") };
        Assert.Equal(CredentialFailureCode.Conflict, (await store.AppendAsync(wrongBinding, default)).Failure!.Code);
        Assert.Equal(CredentialFailureCode.LimitExceeded, (await store.AppendAsync(Evidence(binding, "over-limit"), default)).Failure!.Code);
    }

    private static CredentialRegistryMutation Register(long revision)
    {
        return Register(1, revision);
    }

    private static CredentialRegistryMutation Register(int index, long revision, CredentialProviderLocator? locator = null)
    {
        var referenceId = ReferenceId(index);
        var reference = Reference(referenceId);
        var binding = Binding(referenceId);
        Assert.True(CredentialContractJson.TrySerialize(reference, out _, out var referenceValidation), string.Join(';', referenceValidation.Errors.Select(error => error.Message)));
        Assert.True(CredentialContractJson.TrySerialize(binding, out _, out var bindingValidation), string.Join(';', bindingValidation.Errors.Select(error => error.Message)));
        return new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id($"register-{index}"), revision, referenceId, reference, binding, Id("consent-1"), CredentialProviderHealthStatus.Available, locator ?? Locator());
    }

    private static CredentialRegistryStore Store(WorkspacePaths paths, TimeProvider? timeProvider = null) => new(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), new AcceptingLocatorVerifier(), timeProvider);

    private sealed class AcceptingLocatorVerifier : ICredentialProviderLocatorVerifier
    {
        public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class RecordingLocatorVerifier : ICredentialProviderLocatorVerifier
    {
        public List<string> Locators { get; } = [];

        public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken)
        {
            Locators.Add(locator.Value);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class LongAuthenticationTagTrustProvider(ICapabilityCatalogTrustProvider inner, int maximumAuthenticationTagUtf8Bytes) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes { get; } = maximumAuthenticationTagUtf8Bytes;
        public int InitializeCount { get; private set; }
        public int AuthenticateCount { get; private set; }

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default) => inner.ReadAsync(workspaceIdentity, cancellationToken);

        public async Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            return await inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
        }

        public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
        {
            AuthenticateCount++;
            return Task.FromResult(new string('a', MaximumAuthenticationTagUtf8Bytes));
        }

        public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);
        public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default) => inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
    }

    private sealed class InvalidAuthenticationTagTrustProvider(ICapabilityCatalogTrustProvider inner, string authenticationTag) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => 64;
        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default) => inner.ReadAsync(workspaceIdentity, cancellationToken);
        public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
        public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => Task.FromResult(authenticationTag);
        public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string candidateTag, CancellationToken cancellationToken = default) => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, candidateTag, cancellationToken);
        public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default) => inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
    }

    private sealed class FailOnDurabilityCallBarrier(int failingCall) : ICapabilityCatalogDurabilityBarrier
    {
        private int _callCount;

        public void BeforeDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void AfterDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
        {
        }

        public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
        {
            if (Interlocked.Increment(ref _callCount) == failingCall)
            {
                throw new IOException("Injected credential-registry durability barrier failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private static CredentialReference Reference(CredentialReferenceId? referenceId = null)
    {
        return new CredentialReference(1, referenceId ?? ReferenceId(), "api-token", CredentialLifecycleStatus.Active, "user-1", "Call the example service.", ProviderId("org.example"), new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), null, new Dictionary<string, string> { ["service"] = "Example" });
    }

    private static CredentialCapabilityBinding Binding(CredentialReferenceId? referenceId = null)
    {
        var descriptor = CapabilityCatalogTestData.Descriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        _ = CapabilitySecretRequirement.TryParse("provider-token", out var requirement, out _);
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, descriptor.Implementation, "example", "target", "read", "user-1", null, null);
        return new CredentialCapabilityBinding(1, referenceId ?? ReferenceId(), requirement!, identity!, descriptor.Implementation, scope);
    }

    private static CredentialUseEvidence Evidence(CredentialCapabilityBinding binding, string evidenceId = "evidence-1", CredentialScope? usedScope = null)
    {
        Assert.True(CredentialContractJson.TryHash(binding, out var hash, out _));
        return new CredentialUseEvidence(1, Id(evidenceId), binding.ReferenceId, hash!, Id("proof-1"), Id("run-1"), usedScope ?? binding.Scope, new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), CredentialUseOutcome.Succeeded, true);
    }

    private static CredentialReferenceId ReferenceId()
    {
        return ReferenceId(1);
    }

    private static CredentialReferenceId ReferenceId(int index)
    {
        Assert.True(CredentialReferenceId.TryParse($"credential-{index}", out var value, out _));
        return value!;
    }

    private static CredentialProviderId ProviderId(string value)
    {
        Assert.True(CredentialProviderId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static CredentialContractId Id(string value)
    {
        Assert.True(CredentialContractId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static CredentialProviderLocator Locator(string value = "loc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
    {
        Assert.True(CredentialProviderLocator.TryParse(value, out var parsed));
        return parsed!;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    }
}
