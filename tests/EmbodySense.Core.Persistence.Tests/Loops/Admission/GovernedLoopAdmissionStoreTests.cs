using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Tests.Loops.Admission;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Admission.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionStoreTests
{
    private const string CrossProcessMode = "EMBODYSENSE_ADMISSION_STORE_MODE";
    private const string CrossProcessWorkspace = "EMBODYSENSE_ADMISSION_STORE_WORKSPACE";
    private const string CrossProcessTrustRoot = "EMBODYSENSE_ADMISSION_STORE_TRUST_ROOT";
    private const string CrossProcessGate = "EMBODYSENSE_ADMISSION_STORE_GATE";
    private const string CrossProcessReady = "EMBODYSENSE_ADMISSION_STORE_READY";
    private const string CrossProcessOutput = "EMBODYSENSE_ADMISSION_STORE_OUTPUT";
    private const string CrossProcessOperation = "EMBODYSENSE_ADMISSION_STORE_OPERATION";
    private const char RequestA = '1';
    private const char RequestB = '4';

    [Fact]
    public async Task Commit_restart_read_and_exact_replay_preserve_one_private_immutable_outcome()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = Mutation(paths, "admit-one", RequestA, 0);

        var committed = await Store(paths, trust).CommitAsync(mutation);
        var restarted = Store(paths, trust);
        var read = await restarted.ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId);
        var replayed = await restarted.CommitAsync(mutation);

        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(1, committed.StoreGeneration);
        Assert.Same(mutation.Outcome, committed.Outcome);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Found, read.Status);
        Assert.Equal(1, read.StoreGeneration);
        Assert.Equal(mutation.Outcome.ContentHash, read.Outcome!.ContentHash);
        Assert.NotSame(mutation.Outcome, read.Outcome);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted, replayed.Status);
        Assert.Equal(read.Outcome.ContentHash, replayed.Outcome!.ContentHash);
        Assert.True(File.Exists(PrimaryPath(paths)));
        Assert.True(File.Exists(ProofPath(paths)));
        Assert.False((await File.ReadAllTextAsync(PrimaryPath(paths))).StartsWith('\ufeff'));
    }

    [Fact]
    public async Task Rejected_terminal_outcome_round_trips_without_success_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var mutation = Mutation(paths, "reject-one", RequestA, 0, admitted: false);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());

        var committed = await store.CommitAsync(mutation);
        var read = await store.ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId);

        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(GovernedLoopAdmissionDisposition.Rejected, read.Outcome!.Disposition);
        Assert.Null(read.Outcome.Receipt);
        Assert.NotNull(read.Outcome.Rejection);
        Assert.True(GovernedLoopAdmissionValidator.Validate(read.Outcome).IsValid);
    }

    [Fact]
    public async Task Existing_operation_is_selected_before_generation_and_any_changed_terminal_outcome_conflicts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());
        var first = Mutation(paths, "shared-operation", RequestA, 0);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Committed, (await store.CommitAsync(first)).Status);

        var staleExact = first with { ExpectedStoreGeneration = 0 };
        var changedRequest = Mutation(paths, first.OperationId, RequestB, 1);
        var changedOutcome = Mutation(paths, first.OperationId, RequestA, 1, admitted: false);
        var unrelatedStale = Mutation(paths, "other-operation", RequestB, 0);

        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted, (await store.CommitAsync(staleExact)).Status);
        var requestConflict = await store.CommitAsync(changedRequest);
        var outcomeConflict = await store.CommitAsync(changedOutcome);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.OperationConflict, requestConflict.Status);
        Assert.Equal(first.Outcome.ContentHash, requestConflict.Outcome!.ContentHash);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.OperationConflict, outcomeConflict.Status);
        Assert.Equal(first.Outcome.ContentHash, outcomeConflict.Outcome!.ContentHash);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.GenerationConflict, (await store.CommitAsync(unrelatedStale)).Status);
    }

    [Fact]
    public async Task Concurrent_instances_serialize_one_generation_and_same_exact_outcome_replays()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var first = Mutation(paths, "admit-one", RequestA, 0);
        var other = Mutation(paths, "admit-two", RequestB, 0);

        var distinct = await Task.WhenAll(Store(paths, trust).CommitAsync(first), Store(paths, trust).CommitAsync(other));
        Assert.Single(distinct, item => item.Status == GovernedLoopAdmissionStoreCommitStatus.Committed);
        Assert.Single(distinct, item => item.Status == GovernedLoopAdmissionStoreCommitStatus.GenerationConflict);

        using var replayWorkspace = new TestWorkspace();
        var replayPaths = new WorkspacePaths(replayWorkspace.RootPath);
        var replayTrust = new TestCapabilityLifecycleTrustProvider();
        var exact = Mutation(replayPaths, "admit-same", RequestA, 0);
        var same = await Task.WhenAll(Store(replayPaths, replayTrust).CommitAsync(exact), Store(replayPaths, replayTrust).CommitAsync(exact));
        Assert.Single(same, item => item.Status == GovernedLoopAdmissionStoreCommitStatus.Committed);
        Assert.Single(same, item => item.Status == GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted);
    }

    [Fact]
    public async Task Failure_at_proof_boundary_is_unavailable_and_exact_retry_commits()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = Mutation(paths, "admit-one", RequestA, 0);

        var interrupted = await Store(paths, trust, FailAt(GovernedLoopAdmissionPersistenceBoundary.ProofPublished)).CommitAsync(mutation);
        var retry = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Unavailable, interrupted.Status);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Committed, retry.Status);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Found, (await Store(paths, trust).ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId)).Status);
    }

    [Fact]
    public async Task Published_direct_successor_is_exposed_only_for_matching_operation_and_exact_commit_finalizes_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = Mutation(paths, "admit-one", RequestA, 0);

        var interrupted = await Store(paths, trust, FailAt(GovernedLoopAdmissionPersistenceBoundary.PrimaryPublished)).CommitAsync(mutation);
        var matchingRead = await Store(paths, trust).ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId);
        var unrelatedRead = await Store(paths, trust).ReadByOperationAsync(WorkspaceId(paths), "other-operation");
        var changed = await Store(paths, trust).CommitAsync(Mutation(paths, mutation.OperationId, RequestB, 0));
        var exact = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Recoverable, matchingRead.Status);
        Assert.Equal(mutation.Outcome.ContentHash, matchingRead.Outcome!.ContentHash);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Ambiguous, unrelatedRead.Status);
        Assert.Null(unrelatedRead.Outcome);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.OperationConflict, changed.Status);
        Assert.Equal(mutation.Outcome.ContentHash, changed.Outcome!.ContentHash);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted, exact.Status);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Found, (await Store(paths, trust).ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId)).Status);
    }

    [Fact]
    public async Task Failure_after_trust_advance_is_ambiguous_then_restart_proves_the_outcome()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = Mutation(paths, "admit-one", RequestA, 0);

        var interrupted = await Store(paths, trust, FailAt(GovernedLoopAdmissionPersistenceBoundary.TrustAdvanced)).CommitAsync(mutation);
        var read = await Store(paths, trust).ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId);
        var replay = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Found, read.Status);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted, replay.Status);
    }

    [Fact]
    public async Task Count_and_utf8_quotas_return_exact_limit_without_eviction()
    {
        using var countWorkspace = new TestWorkspace();
        var countPaths = new WorkspacePaths(countWorkspace.RootPath);
        var countTrust = new TestCapabilityLifecycleTrustProvider();
        var countStore = Store(countPaths, countTrust, new GovernedLoopAdmissionStoreOptions { MaxTerminalOutcomes = 1 });
        var first = Mutation(countPaths, "admit-one", RequestA, 0);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Committed, (await countStore.CommitAsync(first)).Status);
        var countResult = await countStore.CommitAsync(Mutation(countPaths, "admit-two", RequestB, 1));
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.LimitExceeded, countResult.Status);
        Assert.Equal(1, countResult.StoreGeneration);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Found, (await countStore.ReadByOperationAsync(WorkspaceId(countPaths), first.OperationId)).Status);

        using var byteWorkspace = new TestWorkspace();
        var bytePaths = new WorkspacePaths(byteWorkspace.RootPath);
        var byteStore = Store(
            bytePaths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopAdmissionStoreOptions { MaxArtifactUtf8Bytes = 1 });
        var byteResult = await byteStore.CommitAsync(Mutation(bytePaths, "admit-one", RequestA, 0));
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.LimitExceeded, byteResult.Status);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.NotFound, (await byteStore.ReadByOperationAsync(WorkspaceId(bytePaths), "admit-one")).Status);
        Assert.False(File.Exists(PrimaryPath(bytePaths)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("enum-case")]
    [InlineData("bom")]
    [InlineData("invalid-utf8")]
    public async Task Noncanonical_or_malformed_primary_never_becomes_current(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = Mutation(paths, "admit-one", RequestA, 0);
        await Store(paths, trust).CommitAsync(mutation);
        var primary = PrimaryPath(paths);
        var text = await File.ReadAllTextAsync(primary);
        switch (corruption)
        {
            case "unknown":
                await File.WriteAllTextAsync(primary, text.Replace("\"workspaceIdentity\":", "\"unknown\": true,\n  \"workspaceIdentity\":", StringComparison.Ordinal));
                break;
            case "duplicate":
                await File.WriteAllTextAsync(primary, text.Replace("\"generation\": 1", "\"generation\": 1,\n  \"generation\": 1", StringComparison.Ordinal));
                break;
            case "enum-case":
                await File.WriteAllTextAsync(primary, text.Replace("\"admitted\"", "\"Admitted\"", StringComparison.Ordinal));
                break;
            case "bom":
                await File.WriteAllBytesAsync(primary, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)]);
                break;
            case "invalid-utf8":
                await File.WriteAllBytesAsync(primary, [0xff, 0xfe, 0xfd]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var read = await Store(paths, trust).ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId);
        var append = await Store(paths, trust).CommitAsync(Mutation(paths, "admit-two", RequestB, 1));
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Ambiguous, read.Status);
        Assert.Null(read.Outcome);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Ambiguous, append.Status);
    }

    [Fact]
    public async Task Untrusted_artifacts_copied_to_another_physical_workspace_fail_closed()
    {
        using var source = new TestWorkspace();
        using var destination = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var destinationPaths = new WorkspacePaths(destination.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        await Store(sourcePaths, trust).CommitAsync(Mutation(sourcePaths, "admit-one", RequestA, 0));
        CopyDirectory(sourcePaths.AgentPath, destinationPaths.AgentPath);

        var read = await Store(destinationPaths, trust).ReadByOperationAsync(WorkspaceId(destinationPaths), "admit-one");
        var commit = await Store(destinationPaths, trust).CommitAsync(Mutation(destinationPaths, "admit-two", RequestB, 0));

        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Unavailable, read.Status);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Unavailable, commit.Status);
    }

    [Fact]
    public async Task Symlinked_admission_parent_fails_closed_without_following_the_target()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        Directory.CreateSymbolicLink(Path.Combine(paths.AgentPath, "loops"), outside.RootPath);

        var result = await Store(paths, new TestCapabilityLifecycleTrustProvider())
            .CommitAsync(Mutation(paths, "admit-one", RequestA, 0));

        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Invalid_coordinates_fail_before_storage_and_wrong_scope_never_reads()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var valid = Mutation(paths, "admit-one", RequestA, 0);
        var invalid = new GovernedLoopAdmissionStoreMutation?[]
        {
            null,
            valid with { WorkspaceId = GovernedLoopAdmissionTestFixture.WorkspaceId },
            valid with { OperationId = "other-operation" },
            valid with { RequestHash = GovernedLoopAdmissionTestFixture.Hash(RequestB) },
            valid with { IntentHash = GovernedLoopAdmissionTestFixture.Hash('f') },
            valid with { ExpectedStoreGeneration = -1 },
            valid with { Outcome = valid.Outcome with { ContentHash = GovernedLoopAdmissionTestFixture.Hash('f') } }
        };

        foreach (var mutation in invalid)
        {
            Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Unavailable, (await store.CommitAsync(mutation!)).Status);
        }

        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Unavailable, (await store.ReadByOperationAsync(GovernedLoopAdmissionTestFixture.WorkspaceId, valid.OperationId)).Status);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Unavailable, (await store.ReadByOperationAsync(WorkspaceId(paths), "INVALID OPERATION")).Status);
        Assert.False(File.Exists(PrimaryPath(paths)));
    }

    [Fact]
    public async Task Required_capabilities_with_empty_resolution_proof_are_rejected_before_storage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = WorkspaceId(paths);
        var valid = Mutation(paths, "admit-operation-1", RequestA, 0);
        var capabilityAdmission = valid.Outcome.Receipt!.Evidence.CapabilityAdmission with
        {
            Pins = [],
            Evidence = []
        };
        var originalEvidence = valid.Outcome.Receipt.Evidence;
        var evidence = new GovernedLoopAdmissionEvidence(
            originalEvidence.SchemaVersion,
            originalEvidence.IntentHash,
            originalEvidence.Binding,
            originalEvidence.EffectiveAuthority,
            capabilityAdmission,
            originalEvidence.References,
            originalEvidence.EvaluatedAtUtc,
            originalEvidence.ContentHash);
        var receipt = valid.Outcome.Receipt with { Evidence = evidence };
        var forged = valid.Outcome with { Receipt = receipt };
        var mutation = new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            valid.OperationId,
            valid.RequestHash,
            valid.IntentHash,
            0,
            forged);

        Assert.False(GovernedLoopAdmissionValidator.Validate(forged).IsValid);
        var result = await Store(paths, new TestCapabilityLifecycleTrustProvider()).CommitAsync(mutation);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Unavailable, result.Status);
        Assert.False(File.Exists(PrimaryPath(paths)));
    }

    [Fact]
    public async Task Cancellation_before_durable_intent_propagates_and_after_proof_is_ambiguous()
    {
        using var firstWorkspace = new TestWorkspace();
        var firstPaths = new WorkspacePaths(firstWorkspace.RootPath);
        var first = Mutation(firstPaths, "admit-one", RequestA, 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(firstPaths, new TestCapabilityLifecycleTrustProvider())
            .CommitAsync(first, new CancellationToken(canceled: true)));
        Assert.False(File.Exists(PrimaryPath(firstPaths)));

        using var secondWorkspace = new TestWorkspace();
        var secondPaths = new WorkspacePaths(secondWorkspace.RootPath);
        var cancellation = new CancellationTokenSource();
        var options = new GovernedLoopAdmissionStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                if (boundary == GovernedLoopAdmissionPersistenceBoundary.ProofPublished)
                {
                    cancellation.Cancel();
                }

                return ValueTask.CompletedTask;
            }
        };
        var result = await Store(secondPaths, new TestCapabilityLifecycleTrustProvider(), options)
            .CommitAsync(Mutation(secondPaths, "admit-one", RequestA, 0), cancellation.Token);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Ambiguous, result.Status);
    }

    [Fact]
    public async Task Shared_authority_transaction_is_reentrant_and_release_failures_preserve_completed_results()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var transaction = new CapabilityAuthorityTransaction(paths);
        var store = new GovernedLoopAdmissionStore(paths, trust, authorityTransaction: transaction);
        var mutation = Mutation(paths, "admit-one", RequestA, 0);

        var committed = await transaction.ExecuteAsync(token => store.CommitAsync(mutation, token));
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Committed, committed.Status);

        var releasing = new GovernedLoopAdmissionStore(paths, trust, authorityTransaction: new ThrowAfterCallbackAuthorityTransaction());
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Found, (await releasing.ReadByOperationAsync(WorkspaceId(paths), mutation.OperationId)).Status);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted, (await releasing.CommitAsync(mutation)).Status);
    }

    [Fact]
    public async Task Foreign_workspace_trust_identity_never_authorizes_read_initialization_or_pending_recovery()
    {
        using var readWorkspace = new TestWorkspace();
        var readPaths = new WorkspacePaths(readWorkspace.RootPath);
        var readTrust = new TestCapabilityLifecycleTrustProvider();
        var readMutation = Mutation(readPaths, "admit-one", RequestA, 0);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Committed, (await Store(readPaths, readTrust).CommitAsync(readMutation)).Status);
        var foreignRead = await Store(readPaths, new ForeignWorkspaceTrustProvider(readTrust, substituteRead: true))
            .ReadByOperationAsync(WorkspaceId(readPaths), readMutation.OperationId);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Unavailable, foreignRead.Status);
        Assert.Null(foreignRead.Outcome);

        using var initializeWorkspace = new TestWorkspace();
        var initializePaths = new WorkspacePaths(initializeWorkspace.RootPath);
        var initializeTrust = new TestCapabilityLifecycleTrustProvider();
        var foreignInitialize = await Store(
                initializePaths,
                new ForeignWorkspaceTrustProvider(initializeTrust, substituteInitialize: true))
            .CommitAsync(Mutation(initializePaths, "admit-one", RequestA, 0));
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.Unavailable, foreignInitialize.Status);
        Assert.False(File.Exists(PrimaryPath(initializePaths)));

        using var pendingWorkspace = new TestWorkspace();
        var pendingPaths = new WorkspacePaths(pendingWorkspace.RootPath);
        var pendingTrust = new TestCapabilityLifecycleTrustProvider();
        var pendingMutation = Mutation(pendingPaths, "admit-one", RequestA, 0);
        Assert.Equal(
            GovernedLoopAdmissionStoreCommitStatus.Ambiguous,
            (await Store(pendingPaths, pendingTrust, FailAt(GovernedLoopAdmissionPersistenceBoundary.PrimaryPublished)).CommitAsync(pendingMutation)).Status);
        var foreignPendingStore = Store(pendingPaths, new ForeignWorkspaceTrustProvider(pendingTrust, substituteRead: true));
        Assert.Equal(
            GovernedLoopAdmissionStoreReadStatus.Unavailable,
            (await foreignPendingStore.ReadByOperationAsync(WorkspaceId(pendingPaths), pendingMutation.OperationId)).Status);
        Assert.Equal(
            GovernedLoopAdmissionStoreCommitStatus.Unavailable,
            (await foreignPendingStore.CommitAsync(pendingMutation)).Status);
    }

    [Fact]
    public void Constructor_rejects_null_impossible_limits_and_overlapping_trust()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopAdmissionStore(null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopAdmissionStore(paths, (ICapabilityCatalogTrustProvider)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopAdmissionStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopAdmissionStoreOptions { MaxTerminalOutcomes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopAdmissionStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopAdmissionStoreOptions { MaxArtifactUtf8Bytes = GovernedLoopAdmissionStoreOptions.MaximumArtifactUtf8Bytes + 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopAdmissionStore(paths, new TestCapabilityLifecycleTrustProvider(0)));
        Assert.Throws<InvalidOperationException>(() => new GovernedLoopAdmissionStore(
            paths,
            new FileCapabilityCatalogTrustProvider(Path.Combine(paths.AgentPath, "server-trust"))));
    }

    [Fact]
    public async Task Cross_process_writers_have_one_generation_winner()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = workspace.File("gate");
        var firstReady = workspace.File("first.ready");
        var secondReady = workspace.File("second.ready");
        var firstOutput = workspace.File("first.output");
        var secondOutput = workspace.File("second.output");
        using var first = StartCrossProcessHost("writer", workspace.RootPath, trustRoot.RootPath, gate, firstReady, firstOutput, "admit-one");
        using var second = StartCrossProcessHost("writer", workspace.RootPath, trustRoot.RootPath, gate, secondReady, secondOutput, "admit-two");
        await Task.WhenAll(WaitForPathAsync(firstReady), WaitForPathAsync(secondReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());
        await Task.WhenAll(AssertProcessSucceededAsync(first), AssertProcessSucceededAsync(second));

        var results = new[] { await File.ReadAllTextAsync(firstOutput), await File.ReadAllTextAsync(secondOutput) };
        Assert.Single(results, item => item == GovernedLoopAdmissionStoreCommitStatus.Committed.ToString());
        Assert.Single(results, item => item == GovernedLoopAdmissionStoreCommitStatus.GenerationConflict.ToString());
    }

    [Theory]
    [InlineData("crash-proof")]
    [InlineData("crash-primary")]
    [InlineData("crash-trust")]
    public async Task Abrupt_process_loss_at_each_durable_boundary_recovers_exactly_once(string mode)
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = workspace.File("gate");
        var ready = workspace.File("ready");
        var output = workspace.File("output");
        using var process = StartCrossProcessHost(mode, workspace.RootPath, trustRoot.RootPath, gate, ready, output, "admit-one");
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);

        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var pending = await Store(paths, trust).ReadByOperationAsync(WorkspaceId(paths), "admit-one");
        var recovered = await Store(paths, trust).CommitAsync(Mutation(paths, "admit-one", RequestA, 0));
        var replayed = await Store(paths, trust).CommitAsync(Mutation(paths, "admit-one", RequestA, 0));

        var expectedRead = mode switch
        {
            "crash-proof" => GovernedLoopAdmissionStoreReadStatus.NotFound,
            "crash-primary" => GovernedLoopAdmissionStoreReadStatus.Recoverable,
            "crash-trust" => GovernedLoopAdmissionStoreReadStatus.Found,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        Assert.Equal(expectedRead, pending.Status);
        Assert.Equal(
            mode == "crash-proof"
                ? GovernedLoopAdmissionStoreCommitStatus.Committed
                : GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted,
            recovered.Status);
        Assert.Equal(GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted, replayed.Status);
        Assert.Equal(1, replayed.StoreGeneration);
    }

    [Fact]
    public async Task Cross_process_admission_store_host()
    {
        var mode = Environment.GetEnvironmentVariable(CrossProcessMode);
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        var workspace = Environment.GetEnvironmentVariable(CrossProcessWorkspace)!;
        var trustRoot = Environment.GetEnvironmentVariable(CrossProcessTrustRoot)!;
        var gate = Environment.GetEnvironmentVariable(CrossProcessGate)!;
        var ready = Environment.GetEnvironmentVariable(CrossProcessReady)!;
        var output = Environment.GetEnvironmentVariable(CrossProcessOutput)!;
        var operation = Environment.GetEnvironmentVariable(CrossProcessOperation)!;
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForPathAsync(gate);
        GovernedLoopAdmissionStoreOptions? options = mode.StartsWith("crash-", StringComparison.Ordinal)
            ? new GovernedLoopAdmissionStoreOptions
            {
                DurableBoundaryObserver = (boundary, _) =>
                {
                    var target = mode switch
                    {
                        "crash-proof" => GovernedLoopAdmissionPersistenceBoundary.ProofPublished,
                        "crash-primary" => GovernedLoopAdmissionPersistenceBoundary.PrimaryPublished,
                        "crash-trust" => GovernedLoopAdmissionPersistenceBoundary.TrustAdvanced,
                        _ => throw new ArgumentOutOfRangeException(nameof(mode))
                    };
                    if (boundary == target)
                    {
                        Process.GetCurrentProcess().Kill();
                        Thread.Sleep(Timeout.Infinite);
                    }

                    return ValueTask.CompletedTask;
                }
            }
            : null;
        var paths = new WorkspacePaths(workspace);
        var store = new GovernedLoopAdmissionStore(paths, new FileCapabilityCatalogTrustProvider(trustRoot), options);
        var mutation = Mutation(paths, operation, operation.EndsWith("two", StringComparison.Ordinal) ? RequestB : RequestA, 0);
        var retryWindow = Stopwatch.StartNew();
        GovernedLoopAdmissionStoreCommitResult result;
        do
        {
            result = await store.CommitAsync(mutation);
            if (mode != "writer"
                || result.Status != GovernedLoopAdmissionStoreCommitStatus.Unavailable
                || retryWindow.Elapsed >= TimeSpan.FromSeconds(15))
            {
                break;
            }

            await Task.Delay(50);
        }
        while (true);
        await File.WriteAllTextAsync(output, result.Status.ToString());
    }

    private static GovernedLoopAdmissionStore Store(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trust,
        GovernedLoopAdmissionStoreOptions? options = null)
        => new(paths, trust, options);

    private static GovernedLoopAdmissionStoreMutation Mutation(
        WorkspacePaths paths,
        string operationId,
        char requestHash,
        long generation,
        bool admitted = true)
    {
        var workspaceId = WorkspaceId(paths);
        var intent = GovernedLoopAdmissionTestFixture.Intent(
            workspaceId: workspaceId,
            operationId: operationId,
            requestHash: GovernedLoopAdmissionTestFixture.Hash(requestHash));
        GovernedLoopAdmissionTerminalOutcome outcome;
        if (admitted)
        {
            var capabilityAdmission = GovernedLoopAdmissionTestFixture.CapabilityAdmission() with { WorkspaceScopeId = workspaceId };
            var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent, capabilityAdmission: capabilityAdmission);
            var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent, evidence);
            outcome = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent, receipt);
        }
        else
        {
            var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
            outcome = GovernedLoopAdmissionTestFixture.RejectedOutcome(intent, rejection);
        }

        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        return new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            operationId,
            intent.RequestHash,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            generation,
            outcome);
    }

    private static string WorkspaceId(WorkspacePaths paths) => CapabilityWorkspaceScopeId.Create(paths.RootPath);

    private static GovernedLoopAdmissionStoreOptions FailAt(GovernedLoopAdmissionPersistenceBoundary boundary)
        => new()
        {
            DurableBoundaryObserver = (observed, _) => observed == boundary
                ? ValueTask.FromException(new IOException("Injected admission durable-boundary interruption."))
                : ValueTask.CompletedTask
        };

    private static string PrimaryPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "admissions", "terminal-outcomes.json");

    private static string ProofPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "admissions", "terminal-outcomes.proved.json");

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static Process StartCrossProcessHost(
        string mode,
        string workspace,
        string trustRoot,
        string gate,
        string ready,
        string output,
        string operation)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Verification.CoverageChildProcessAssembly.AddVstestArguments(
            startInfo,
            typeof(GovernedLoopAdmissionStoreTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Loops.Admission.GovernedLoopAdmissionStoreTests.Cross_process_admission_store_host");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessMode] = mode;
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessTrustRoot] = trustRoot;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOutput] = output;
        startInfo.Environment[CrossProcessOperation] = operation;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process admission-store host did not start.");
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(15), $"Cross-process admission host did not publish `{path}`.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertProcessSucceededAsync(Process process)
    {
        var error = await process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error + Environment.NewLine + output);
    }

    private sealed class ThrowAfterCallbackAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public async Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            _ = await operation(cancellationToken);
            throw new IOException("Injected authority-release failure.");
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
            Func<CancellationToken, Task<bool>> validator,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ForeignWorkspaceTrustProvider(
        ICapabilityCatalogTrustProvider inner,
        bool substituteRead = false,
        bool substituteInitialize = false) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

        public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

        public async Task<CapabilityCatalogTrustState?> ReadAsync(
            string workspaceIdentity,
            CancellationToken cancellationToken = default)
        {
            var state = await inner.ReadAsync(workspaceIdentity, cancellationToken);
            return state is not null && substituteRead
                ? state with { WorkspaceIdentity = "sha256:" + new string('f', 64) }
                : state;
        }

        public async Task<CapabilityCatalogTrustState> InitializeAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
        {
            var state = await inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
            return substituteInitialize
                ? state with { WorkspaceIdentity = "sha256:" + new string('f', 64) }
                : state;
        }

        public Task<string> AuthenticateArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<bool> VerifyArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            string authenticationTag,
            CancellationToken cancellationToken = default)
            => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

        public Task<CapabilityCatalogTrustState> AdvanceAsync(
            string workspaceIdentity,
            long expectedGeneration,
            string expectedContentDigest,
            long newGeneration,
            string newContentDigest,
            CancellationToken cancellationToken = default)
            => inner.AdvanceAsync(
                workspaceIdentity,
                expectedGeneration,
                expectedContentDigest,
                newGeneration,
                newContentDigest,
                cancellationToken);
    }
}
