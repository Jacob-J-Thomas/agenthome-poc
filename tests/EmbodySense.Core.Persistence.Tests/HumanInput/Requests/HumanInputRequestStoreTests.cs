using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequestStoreTestData;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Requests;

public sealed class HumanInputRequestStoreTests
{
    [Fact]
    public async Task Commit_restart_read_and_exact_replay_preserve_one_private_immutable_request()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();

        var committed = await Store(paths, trust).CommitAsync(mutation);
        var restarted = Store(paths, trust);
        var read = await restarted.ReadAsync("request-one");
        var mutationRead = await restarted.ReadForMutationAsync("request-one", "create-one", HashA);
        var replayed = await restarted.CommitAsync(mutation);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(1, committed.StoreGeneration);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, read.Status);
        Assert.Equal(mutation.PrimaryHeadToWrite, read.PrimarySnapshot!.Head);
        Assert.Equivalent(mutation.RequestToAppend, Assert.Single(read.PrimarySnapshot.RequestVersions), strict: true);
        Assert.Equal(mutation.Operation, Assert.Single(read.PrimarySnapshot.Operations));
        Assert.Equal(mutation.Operation, mutationRead.ExistingOperation!.Evidence);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(1, replayed.StoreGeneration);
    }

    [Fact]
    public async Task Changed_global_operation_intent_conflicts_and_stale_generation_cannot_append()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var first = CreateMutation(operationId: "shared-operation");
        var changed = CreateMutation("request-two", "version-two", "shared-operation", HashB, 1);
        var stale = CreateMutation("request-three", "version-three", "create-three", HashC, 0);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(first)).Status);
        var conflict = await store.CommitAsync(changed);
        var staleResult = await store.CommitAsync(stale);
        var changedRead = await store.ReadForMutationAsync("request-two", "shared-operation", HashB);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.OperationConflict, conflict.Status);
        Assert.Equal("request-one", conflict.StoredOperation!.RequestId);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.OperationConflict, changedRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.StoreConflict, staleResult.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.NotFound, (await store.ReadAsync("request-two")).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.NotFound, (await store.ReadAsync("request-three")).Status);
    }

    [Fact]
    public async Task Concurrent_instances_serialize_one_generation_and_exact_same_intent_replays()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var first = Store(paths, trust);
        var second = Store(paths, trust);
        var outcomes = await Task.WhenAll(
            first.CommitAsync(CreateMutation()),
            second.CommitAsync(CreateMutation("request-two", "version-two", "create-two", HashB)));

        Assert.Single(outcomes, outcome => outcome.Status == HumanInputRequestLifecycleStoreCommitStatus.Committed);
        Assert.Single(outcomes, outcome => outcome.Status == HumanInputRequestLifecycleStoreCommitStatus.StoreConflict);

        using var replayWorkspace = new TestWorkspace();
        var replayPaths = new WorkspacePaths(replayWorkspace.RootPath);
        var replayTrust = new TestCapabilityLifecycleTrustProvider();
        var exact = CreateMutation();
        var replays = await Task.WhenAll(
            Store(replayPaths, replayTrust).CommitAsync(exact),
            Store(replayPaths, replayTrust).CommitAsync(exact));
        Assert.Single(replays, outcome => outcome.Status == HumanInputRequestLifecycleStoreCommitStatus.Committed);
        Assert.Single(replays, outcome => outcome.Status == HumanInputRequestLifecycleStoreCommitStatus.Replayed);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Remind, HumanInputRequestLifecycleStatus.Pending, 1, 1)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestLifecycleStatus.Pending, 0, 2)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend, HumanInputRequestLifecycleStatus.Pending, 0, 2)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, HumanInputRequestLifecycleStatus.Rejected, 0, 1)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleStatus.Cancelled, 0, 1)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, HumanInputRequestLifecycleStatus.Expired, 0, 1)]
    public async Task Every_single_request_lifecycle_transition_is_append_only_and_restart_safe(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleStatus expectedStatus,
        int expectedReminders,
        int expectedVersions)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        var transition = TransitionMutation(kind, created.RequestToAppend!, created.PrimaryHeadToWrite!, 1, "operation-two", HashB);

        var committed = await store.CommitAsync(transition);
        var read = await Store(paths, trust).ReadAsync("request-one");

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(expectedStatus, read.PrimarySnapshot!.Head.Status);
        Assert.Equal(expectedReminders, read.PrimarySnapshot.Head.ReminderCount);
        Assert.Equal(expectedVersions, read.PrimarySnapshot.RequestVersions.Count);
        Assert.Equal(2, read.PrimarySnapshot.Operations.Count);
        Assert.Equal(transition.Operation, read.PrimarySnapshot.Operations[^1]);
    }

    [Fact]
    public async Task Supersede_atomically_appends_candidate_and_two_reciprocal_heads()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        await store.CommitAsync(created);
        var supersede = SupersedeMutation(created.RequestToAppend!, created.PrimaryHeadToWrite!, 1);

        var committed = await store.CommitAsync(supersede);
        var oldRequest = await Store(paths, trust).ReadAsync("request-one");
        var newRequest = await Store(paths, trust).ReadAsync("request-two");
        var mutationRead = await Store(paths, trust).ReadForMutationAsync("request-one", "supersede-one", HashB);
        var replayed = await Store(paths, trust).CommitAsync(supersede);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Superseded, oldRequest.PrimarySnapshot!.Head.Status);
        Assert.Equal("request-two", oldRequest.PrimarySnapshot.Head.SupersededByRequestId);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, newRequest.PrimarySnapshot!.Head.Status);
        Assert.Equal("request-one", newRequest.PrimarySnapshot.Head.SupersedesRequestId);
        Assert.Equal(supersede.SecondaryHeadToWrite, mutationRead.RelatedSnapshot!.Head);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(supersede.PrimaryHeadToWrite, replayed.PrimarySnapshot!.Head);
        Assert.Equal(supersede.SecondaryHeadToWrite, replayed.RelatedSnapshot!.Head);
    }

    [Fact]
    public async Task Supersede_planning_atomically_observes_an_existing_candidate_and_replays_deterministic_conflict()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var target = CreateMutation();
        var existingCandidate = CreateMutation("request-two", "version-existing", "create-two", HashB, 1);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(target)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(existingCandidate)).Status);

        var planning = await store.ReadForMutationAsync(
            "request-one",
            "supersede-conflict",
            HashC,
            "request-two");
        var intendedCandidate = Request(
            "request-two",
            "version-intended",
            Time.AddMinutes(1),
            target.RequestToAppend!.Binding,
            prompt: "Private intended replacement prompt.");
        var evidence = Evidence(
            HumanInputRequestLifecycleOperationKind.Supersede,
            "request-one",
            "supersede-conflict",
            HashC,
            Time.AddMinutes(1),
            target.PrimaryHeadToWrite,
            target.PrimaryHeadToWrite,
            intendedCandidate,
            "request-two",
            existingCandidate.PrimaryHeadToWrite,
            existingCandidate.PrimaryHeadToWrite,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        var receipt = new HumanInputRequestLifecycleStoreMutation(2, evidence, null, null, null);

        var committed = await store.CommitAsync(receipt);
        var restarted = Store(paths, trust);
        var exact = await restarted.ReadForMutationAsync(
            "request-one",
            "supersede-conflict",
            HashC,
            "request-two");
        var legacyExact = await restarted.ReadForMutationAsync("request-one", "supersede-conflict", HashC);
        var changedRelation = await restarted.ReadForMutationAsync(
            "request-one",
            "supersede-conflict",
            HashC,
            "request-three");

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, planning.Status);
        Assert.Equal(2, planning.StoreGeneration);
        Assert.Equal(target.PrimaryHeadToWrite, planning.PrimarySnapshot!.Head);
        Assert.Equal(existingCandidate.PrimaryHeadToWrite, planning.RelatedSnapshot!.Head);
        Assert.Null(planning.ExistingOperation);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, exact.Status);
        Assert.Equal(evidence, exact.ExistingOperation!.Evidence);
        Assert.Equal(existingCandidate.PrimaryHeadToWrite, exact.RelatedSnapshot!.Head);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, legacyExact.Status);
        Assert.Equal(existingCandidate.PrimaryHeadToWrite, legacyExact.RelatedSnapshot!.Head);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.OperationConflict, changedRelation.Status);
        Assert.Equal(evidence, changedRelation.ExistingOperation!.Evidence);
        Assert.Null(changedRelation.RelatedSnapshot);
    }

    [Fact]
    public async Task Terminal_failure_receipt_is_durable_evidence_without_rewriting_the_head()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        await store.CommitAsync(created);
        var receipt = ReceiptMutation(
            HumanInputRequestLifecycleOperationKind.Cancel,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict,
            created.PrimaryHeadToWrite,
            1,
            "cancel-conflict",
            HashB);

        var committed = await store.CommitAsync(receipt);
        var read = await Store(paths, trust).ReadAsync("request-one");

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(created.PrimaryHeadToWrite, read.PrimarySnapshot!.Head);
        Assert.Equal(receipt.Operation, read.PrimarySnapshot.Operations[^1]);
        Assert.Equal(2, read.StoreGeneration);
    }

    [Fact]
    public async Task Request_version_identity_cannot_be_rebound_to_changed_content_and_exact_replay_remains_valid()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        var validAmend = TransitionMutation(
            HumanInputRequestLifecycleOperationKind.Amend,
            created.RequestToAppend!,
            created.PrimaryHeadToWrite!,
            1,
            "amend-one",
            HashB);
        var reboundRequest = Rehash(validAmend.RequestToAppend! with { RequestVersionId = "version-one" });
        var reboundHead = validAmend.PrimaryHeadToWrite! with { CurrentRequest = Reference(reboundRequest) };
        var reboundEvidence = validAmend.Operation with
        {
            CandidateRequest = Reference(reboundRequest),
            ResultHead = reboundHead
        };
        var rebound = validAmend with
        {
            Operation = reboundEvidence,
            RequestToAppend = reboundRequest,
            PrimaryHeadToWrite = reboundHead
        };

        var rejected = await store.CommitAsync(rebound);
        var unchanged = await store.ReadAsync("request-one");
        var replayed = await store.CommitAsync(created);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, rejected.Status);
        Assert.Single(unchanged.PrimarySnapshot!.RequestVersions);
        Assert.Single(unchanged.PrimarySnapshot.Operations);
        Assert.Equal(created.PrimaryHeadToWrite, unchanged.PrimarySnapshot.Head);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, replayed.Status);
    }

    [Fact]
    public async Task Mutation_and_result_snapshots_do_not_alias_caller_owned_arrays_or_expose_private_prompt_in_strings()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation(prompt: "do-not-log-this-private-prompt");
        var respondents = mutation.RequestToAppend!.EligibleRespondents;

        var committed = await Store(paths, trust).CommitAsync(mutation);
        respondents[0] = respondents[0] with { RespondentId = "attacker" };
        var read = await Store(paths, trust).ReadAsync("request-one");

        Assert.Equal("user-one", read.PrimarySnapshot!.RequestVersions[0].EligibleRespondents[0].RespondentId);
        Assert.DoesNotContain("do-not-log-this-private-prompt", mutation.RequestToAppend.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-private-prompt", committed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-private-prompt", read.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HumanInputRequestPersistenceBoundary.TrustInitialized, HumanInputRequestLifecycleStoreCommitStatus.Unavailable, HumanInputRequestLifecycleStoreCommitStatus.Committed)]
    [InlineData(HumanInputRequestPersistenceBoundary.ProofPublished, HumanInputRequestLifecycleStoreCommitStatus.Unavailable, HumanInputRequestLifecycleStoreCommitStatus.Committed)]
    [InlineData(HumanInputRequestPersistenceBoundary.PrimaryPublished, HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, HumanInputRequestLifecycleStoreCommitStatus.Replayed)]
    [InlineData(HumanInputRequestPersistenceBoundary.TrustAdvanced, HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, HumanInputRequestLifecycleStoreCommitStatus.Replayed)]
    public async Task Every_durable_boundary_has_one_explicit_exact_retry_outcome(
        HumanInputRequestPersistenceBoundary boundary,
        HumanInputRequestLifecycleStoreCommitStatus interruptedStatus,
        HumanInputRequestLifecycleStoreCommitStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();

        var interrupted = await Store(paths, trust, FailAt(boundary)).CommitAsync(mutation);
        var unrelated = await Store(paths, trust).ReadForMutationAsync("request-two", "create-one", HashA);
        var exact = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(interruptedStatus, interrupted.Status);
        if (boundary == HumanInputRequestPersistenceBoundary.PrimaryPublished)
        {
            Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, unrelated.Status);
        }

        Assert.Equal(retryStatus, exact.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, (await Store(paths, trust).ReadAsync("request-one")).Status);
    }

    [Fact]
    public async Task Fresh_commit_rejects_no_op_trust_advancement_as_ambiguous_and_exact_retry_recovers()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();
        var malformed = new HumanInputRequestStore(paths, new HumanInputNoOpAdvanceTrustProvider(trust));

        var first = await malformed.CommitAsync(mutation);
        var ordinaryRead = await Store(paths, trust).ReadAsync("request-one");
        var retry = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, first.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, ordinaryRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, retry.Status);
        Assert.Single(retry.PrimarySnapshot!.Operations);
    }

    [Fact]
    public async Task Pending_retry_rejects_malformed_returned_trust_state_as_ambiguous_before_replay()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();
        Assert.Equal(
            HumanInputRequestLifecycleStoreCommitStatus.Ambiguous,
            (await Store(paths, trust, FailAt(HumanInputRequestPersistenceBoundary.PrimaryPublished)).CommitAsync(mutation)).Status);
        var malformed = new HumanInputRequestStore(paths, new HumanInputMalformedAdvanceTrustProvider(trust));

        var firstRetry = await malformed.CommitAsync(mutation);
        var exactReplay = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, firstRetry.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, exactReplay.Status);
        Assert.Single(exactReplay.PrimarySnapshot!.Operations);
    }

    [Fact]
    public async Task Published_direct_successor_is_finalized_only_by_exact_request_operation_and_hash()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();
        var interrupted = await Store(paths, trust, FailAt(HumanInputRequestPersistenceBoundary.PrimaryPublished)).CommitAsync(mutation);

        var ordinary = await Store(paths, trust).ReadAsync("request-one");
        var wrongRequest = await Store(paths, trust).ReadForMutationAsync("request-two", "create-one", HashA);
        var wrongOperation = await Store(paths, trust).ReadForMutationAsync("request-one", "create-two", HashA);
        var wrongHash = await Store(paths, trust).ReadForMutationAsync("request-one", "create-one", HashB);
        var exact = await Store(paths, trust).ReadForMutationAsync("request-one", "create-one", HashA);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, ordinary.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, wrongRequest.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, wrongOperation.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, wrongHash.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, exact.Status);
        Assert.Equal(mutation.Operation, exact.ExistingOperation!.Evidence);
    }

    [Fact]
    public async Task Pre_cancelled_operations_propagate_without_publishing_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());
        var cancellation = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitAsync(CreateMutation(), cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync("request-one", cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReadForMutationAsync("request-one", "create-one", HashA, cancellationToken: cancellation));

        Assert.False(File.Exists(PrimaryPath(paths)));
        Assert.False(File.Exists(ProofPath(paths)));
    }

    [Fact]
    public async Task Internal_cancellation_without_caller_cancellation_fails_closed_across_public_operations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        var transition = TransitionMutation(
            HumanInputRequestLifecycleOperationKind.Remind,
            created.RequestToAppend!,
            created.PrimaryHeadToWrite!,
            1,
            "remind-one",
            HashB);
        trust.BeforeRead = _ => throw new OperationCanceledException(new CancellationToken(canceled: true));

        var read = await store.ReadAsync("request-one");
        var mutationRead = await store.ReadForMutationAsync("request-one", "remind-one", HashB);
        var commit = await store.CommitAsync(transition);

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, read.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, mutationRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, commit.Status);
    }

    [Fact]
    public async Task Cancellation_after_primary_publication_is_ambiguous_and_exact_retry_recovers()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();
        using var cancellation = new CancellationTokenSource();
        var options = new HumanInputRequestStoreOptions
        {
            DurableBoundaryObserver = (boundary, token) =>
            {
                if (boundary == HumanInputRequestPersistenceBoundary.PrimaryPublished)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return ValueTask.CompletedTask;
            }
        };

        var interrupted = await Store(paths, trust, options).CommitAsync(mutation, cancellation.Token);
        var replayed = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, replayed.Status);
    }

    [Fact]
    public async Task Primary_rename_durability_failure_is_ambiguous_and_direct_retry_finalizes_once()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();
        var interrupted = new HumanInputRequestStore(
            paths,
            trust,
            durabilityBarrier: new HumanInputFailAfterPrimaryRenameBarrier());

        var first = await interrupted.CommitAsync(mutation);
        var recovered = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, first.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, recovered.Status);
        Assert.Single(recovered.PrimarySnapshot!.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outer_authority_release_failure_preserves_completed_public_result(bool read)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await Store(paths, trust).CommitAsync(mutation)).Status);
        var transaction = new HumanInputPostCallbackAuthorityTransaction(new IOException("Injected authority-release failure."));
        var releasing = new HumanInputRequestStore(paths, trust, authorityTransaction: transaction);

        if (read)
        {
            var result = await releasing.ReadAsync("request-one");
            Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, result.Status);
            Assert.Equal(mutation.PrimaryHeadToWrite, result.PrimarySnapshot!.Head);
        }
        else
        {
            var result = await releasing.CommitAsync(mutation);
            Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, result.Status);
            Assert.Equal(mutation.Operation, result.StoredOperation!.Evidence);
        }
    }

    [Fact]
    public async Task Invalid_public_inputs_fail_closed_without_entering_persistence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());
        var mutation = CreateMutation();

        var invalidRead = await store.ReadAsync("NOT CANONICAL");
        var invalidMutationRead = await store.ReadForMutationAsync("request-one", "BAD OPERATION", HashA);
        var invalidHashRead = await store.ReadForMutationAsync("request-one", "create-one", "short");
        var invalidRelatedRead = await store.ReadForMutationAsync("request-one", "create-one", HashA, "NOT CANONICAL");
        var selfRelatedRead = await store.ReadForMutationAsync("request-one", "create-one", HashA, "request-one");
        var nullCommit = await store.CommitAsync(null!);
        var invalidCommit = await store.CommitAsync(mutation with { ExpectedStoreGeneration = -1 });

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, invalidRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, invalidMutationRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, invalidHashRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, invalidRelatedRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, selfRelatedRead.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, nullCommit.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, invalidCommit.Status);
        Assert.False(File.Exists(PrimaryPath(paths)));
    }

    [Fact]
    public async Task Configured_no_eviction_quotas_reject_growth_without_rewriting_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var options = new HumanInputRequestStoreOptions { MaxRequests = 1, MaxRequestVersions = 1, MaxOperations = 1 };
        var store = Store(paths, trust, options);
        var created = CreateMutation();
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);

        var secondRequest = await store.CommitAsync(CreateMutation("request-two", "version-two", "create-two", HashB, 1));
        var amended = await store.CommitAsync(TransitionMutation(
            HumanInputRequestLifecycleOperationKind.Amend,
            created.RequestToAppend!,
            created.PrimaryHeadToWrite!,
            1,
            "amend-one",
            HashB));
        var receipt = await store.CommitAsync(ReceiptMutation(
            HumanInputRequestLifecycleOperationKind.Cancel,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict,
            created.PrimaryHeadToWrite,
            1,
            "cancel-conflict",
            HashC));

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded, secondRequest.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded, amended.Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded, receipt.Status);
        var read = await Store(paths, trust, options).ReadAsync("request-one");
        Assert.Equal(1, read.StoreGeneration);
        Assert.Single(read.PrimarySnapshot!.RequestVersions);
        Assert.Single(read.PrimarySnapshot.Operations);
    }

    [Fact]
    public async Task Artifact_byte_limit_is_explicit_and_does_not_publish_partial_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var options = new HumanInputRequestStoreOptions { MaxArtifactUtf8Bytes = 128 };

        var result = await Store(paths, trust, options).CommitAsync(CreateMutation());

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded, result.Status);
        Assert.False(File.Exists(PrimaryPath(paths)));
    }

    [Fact]
    public async Task Unknown_duplicate_case_changed_quoted_number_and_noncanonical_enum_json_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        await Store(paths, trust).CommitAsync(CreateMutation());
        var path = PrimaryPath(paths);
        var original = await File.ReadAllTextAsync(path);
        var corruptions = new[]
        {
            original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"authority\": \"ambient\"", StringComparison.Ordinal),
            original.Replace("\"generation\": 1", "\"generation\": 1, \"generation\": 1", StringComparison.Ordinal),
            original.Replace("\"generation\": 1", "\"Generation\": 1", StringComparison.Ordinal),
            original.Replace("\"generation\": 1", "\"generation\": \"1\"", StringComparison.Ordinal),
            original.Replace("\"status\": \"pending\"", "\"status\": \"Pending\"", StringComparison.Ordinal),
            original.Replace("\"kind\": \"create\"", "\"kind\": 1", StringComparison.Ordinal)
        };

        foreach (var corruption in corruptions)
        {
            Assert.NotEqual(original, corruption);
            await File.WriteAllTextAsync(path, corruption);
            var read = await Store(paths, trust).ReadAsync("request-one");
            Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, read.Status);
        }
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("bom")]
    [InlineData("truncated")]
    [InlineData("oversize")]
    public async Task Malformed_or_unbounded_primary_artifacts_fail_closed_without_partial_rehydration(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        await Store(paths, trust).CommitAsync(CreateMutation());
        var path = PrimaryPath(paths);
        var bytes = await File.ReadAllBytesAsync(path);
        var replacement = corruption switch
        {
            "invalid-utf8" => [.. bytes, (byte)0xff],
            "bom" => [.. Encoding.UTF8.GetPreamble(), .. bytes],
            "truncated" => bytes[..(bytes.Length / 2)],
            "oversize" => new byte[4 * 1024 * 1024 + 1],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        await File.WriteAllBytesAsync(path, replacement);

        var read = await Store(paths, trust).ReadAsync("request-one");

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, read.Status);
        Assert.Null(read.PrimarySnapshot);
    }

    [Fact]
    public async Task Authenticated_but_impossible_lineage_and_generation_are_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var created = CreateMutation();
        await Store(paths, trust).CommitAsync(created);
        var pinned = await RewriteAuthenticatedAsync(paths, root =>
        {
            root["generation"] = 2L;
            root["heads"]!.AsArray()[0]!.AsObject()["lifecycleVersion"] = 2L;
        });

        var read = await Store(paths, pinned).ReadAsync("request-one");

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.PrimarySnapshot);
    }

    [Fact]
    public async Task Authenticated_history_rebinding_one_version_identity_to_changed_content_is_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateMutation();
        await store.CommitAsync(created);
        var amended = TransitionMutation(
            HumanInputRequestLifecycleOperationKind.Amend,
            created.RequestToAppend!,
            created.PrimaryHeadToWrite!,
            1,
            "amend-one",
            HashB);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(amended)).Status);
        var rebound = Rehash(amended.RequestToAppend! with { RequestVersionId = "version-one" });
        var originalHash = amended.RequestToAppend!.RequestHash;
        var pinned = await RewriteAuthenticatedAsync(paths, root =>
        {
            var json = root.ToJsonString()
                .Replace("version-amended", "version-one", StringComparison.Ordinal)
                .Replace(originalHash, rebound.RequestHash, StringComparison.Ordinal);
            root.Clear();
            foreach (var property in JsonNode.Parse(json)!.AsObject())
            {
                root[property.Key] = property.Value?.DeepClone();
            }
        });

        var read = await Store(paths, pinned).ReadAsync("request-one");

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.PrimarySnapshot);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("purpose")]
    [InlineData("grant-id")]
    [InlineData("grant-revision")]
    public async Task Authenticated_noncanonical_authority_values_are_rejected(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        await Store(paths, trust).CommitAsync(CreateMutation());
        var pinned = await RewriteAuthenticatedAsync(paths, root =>
        {
            var operation = root["operations"]!.AsArray()[0]!.AsObject()["requestLifecycle"]!.AsObject();
            switch (corruption)
            {
                case "actor":
                    operation["actorId"] = "NOT CANONICAL";
                    break;
                case "purpose":
                    operation["reason"] = "\u0001";
                    break;
                case "grant-id":
                    operation["grantReference"]!.AsObject()["grantId"] = "NOT CANONICAL";
                    break;
                case "grant-revision":
                    operation["grantReference"]!.AsObject()["revision"] = "01";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption));
            }
        });

        var read = await Store(paths, pinned).ReadAsync("request-one");

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.PrimarySnapshot);
    }

    [Fact]
    public async Task Copied_workspace_artifacts_do_not_inherit_server_trust()
    {
        using var source = new TestWorkspace();
        using var destination = new TestWorkspace();
        var trust = new TestCapabilityLifecycleTrustProvider();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var destinationPaths = new WorkspacePaths(destination.RootPath);
        await Store(sourcePaths, trust).CommitAsync(CreateMutation());
        CopyDirectory(sourcePaths.AgentPath, destinationPaths.AgentPath);

        var copied = await Store(destinationPaths, trust).ReadAsync("request-one");

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Unavailable, copied.Status);
    }

    [Fact]
    public async Task Symlinked_human_input_directory_fails_closed_without_outside_write()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        Directory.CreateSymbolicLink(Path.Combine(paths.AgentPath, "human-input"), outside.RootPath);

        var result = await Store(paths, new TestCapabilityLifecycleTrustProvider()).CommitAsync(CreateMutation());

        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Constructor_rejects_null_impossible_bounds_and_overlapping_trust_root()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        Assert.Throws<ArgumentNullException>(() => new HumanInputRequestStore(null!));
        Assert.Throws<ArgumentNullException>(() => new HumanInputRequestStore(paths, (ICapabilityCatalogTrustProvider)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HumanInputRequestStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new HumanInputRequestStoreOptions { MaxRequests = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HumanInputRequestStore(paths, new TestCapabilityLifecycleTrustProvider(0)));
        Assert.Throws<InvalidOperationException>(() => new HumanInputRequestStore(
            paths,
            new FileCapabilityCatalogTrustProvider(Path.Combine(paths.AgentPath, "server-trust"))));
    }

    [Fact]
    public async Task Cross_process_writers_have_one_generation_winner()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = Path.Combine(workspace.RootPath, "release-human-input-writers");
        var firstReady = Path.Combine(workspace.RootPath, "human-input-first-ready");
        var secondReady = Path.Combine(workspace.RootPath, "human-input-second-ready");
        var firstOutput = Path.Combine(workspace.RootPath, "human-input-first-output");
        var secondOutput = Path.Combine(workspace.RootPath, "human-input-second-output");
        using var first = StartCrossProcessHost("writer", workspace.RootPath, trustRoot.RootPath, gate, firstReady, firstOutput, "request-one", "create-one", HashA);
        using var second = StartCrossProcessHost("writer", workspace.RootPath, trustRoot.RootPath, gate, secondReady, secondOutput, "request-two", "create-two", HashB);

        await Task.WhenAll(WaitForPathAsync(firstReady), WaitForPathAsync(secondReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProcessSucceededAsync(first);
        await AssertProcessSucceededAsync(second);
        var statuses = new[] { await File.ReadAllTextAsync(firstOutput), await File.ReadAllTextAsync(secondOutput) };

        Assert.Single(statuses, status => status == HumanInputRequestLifecycleStoreCommitStatus.Committed.ToString());
        Assert.Single(statuses, status => status == HumanInputRequestLifecycleStoreCommitStatus.StoreConflict.ToString());
    }

    [Fact]
    public async Task Cross_process_related_read_is_atomic_with_candidate_creation_and_restart_observes_one_coherent_state()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Equal(
            HumanInputRequestLifecycleStoreCommitStatus.Committed,
            (await new HumanInputRequestStore(paths, provider).CommitAsync(CreateMutation())).Status);
        var gate = Path.Combine(workspace.RootPath, "release-human-input-related-race");
        var readerReady = Path.Combine(workspace.RootPath, "human-input-related-reader-ready");
        var writerReady = Path.Combine(workspace.RootPath, "human-input-related-writer-ready");
        var readerOutput = Path.Combine(workspace.RootPath, "human-input-related-reader-output");
        var writerOutput = Path.Combine(workspace.RootPath, "human-input-related-writer-output");
        using var reader = StartCrossProcessHost(
            "related-reader",
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            readerReady,
            readerOutput,
            "request-one",
            "supersede-race",
            HashC,
            relatedRequestId: "request-two");
        using var writer = StartCrossProcessHost(
            "writer",
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            writerReady,
            writerOutput,
            "request-two",
            "create-two",
            HashB,
            generation: 1);

        await Task.WhenAll(WaitForPathAsync(readerReady), WaitForPathAsync(writerReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(reader.WaitForExitAsync(), writer.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProcessSucceededAsync(reader);
        await AssertProcessSucceededAsync(writer);
        var readerResult = await File.ReadAllTextAsync(readerOutput);
        var writerResult = await File.ReadAllTextAsync(writerOutput);

        Assert.Contains(readerResult, new[] { "Ready|1|False", "Ready|2|True" });
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed.ToString(), writerResult);
        var restarted = new HumanInputRequestStore(paths, provider);
        var finalPlanning = await restarted.ReadForMutationAsync(
            "request-one",
            "supersede-after-race",
            HashC,
            "request-two");
        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, finalPlanning.Status);
        Assert.Equal(2, finalPlanning.StoreGeneration);
        Assert.NotNull(finalPlanning.RelatedSnapshot);
        Assert.Equal("request-two", finalPlanning.RelatedSnapshot.Head.RequestId);
    }

    [Theory]
    [InlineData(HumanInputRequestPersistenceBoundary.TrustInitialized)]
    [InlineData(HumanInputRequestPersistenceBoundary.ProofPublished)]
    [InlineData(HumanInputRequestPersistenceBoundary.PrimaryPublished)]
    [InlineData(HumanInputRequestPersistenceBoundary.TrustAdvanced)]
    public async Task Abrupt_process_loss_at_every_boundary_recovers_to_exact_once(HumanInputRequestPersistenceBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = Path.Combine(workspace.RootPath, "release-human-input-crash");
        var ready = Path.Combine(workspace.RootPath, "human-input-crash-ready");
        var output = Path.Combine(workspace.RootPath, "human-input-crash-output");
        using var process = StartCrossProcessHost(
            "crash",
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            ready,
            output,
            "request-one",
            "create-one",
            HashA,
            boundary);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(trustRoot.RootPath));

        var recovered = await store.CommitAsync(CreateMutation());
        var replay = await store.CommitAsync(CreateMutation());

        Assert.Contains(recovered.Status, new[]
        {
            HumanInputRequestLifecycleStoreCommitStatus.Committed,
            HumanInputRequestLifecycleStoreCommitStatus.Replayed
        });
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Replayed, replay.Status);
        Assert.Single((await store.ReadAsync("request-one")).PrimarySnapshot!.Operations);
    }

    private static HumanInputRequestStore Store(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trust,
        HumanInputRequestStoreOptions? options = null)
        => new(paths, trust, options);

    private static HumanInputRequestStoreOptions FailAt(HumanInputRequestPersistenceBoundary target)
        => new()
        {
            DurableBoundaryObserver = (boundary, _) => boundary == target
                ? ValueTask.FromException(new IOException("Injected durable-boundary interruption."))
                : ValueTask.CompletedTask
        };

    private static string PrimaryPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "human-input", "requests", "lifecycle.json");

    private static string ProofPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "human-input", "requests", "lifecycle.proved.json");

    private static async Task<ICapabilityCatalogTrustProvider> RewriteAuthenticatedAsync(
        WorkspacePaths paths,
        Action<JsonObject> mutate)
    {
        var path = PrimaryPath(paths);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        mutate(root);
        root["contentDigest"] = string.Empty;
        root["authenticationTag"] = string.Empty;
        var canonical = root.ToJsonString();
        var contentDigest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(canonical)).Value;
        const string AuthenticationTag = "pinned-human-input-document";
        root["contentDigest"] = contentDigest;
        root["authenticationTag"] = AuthenticationTag;
        await File.WriteAllTextAsync(path, root.ToJsonString() + Environment.NewLine);
        return new HumanInputPinnedTrustProvider(
            root["workspaceIdentity"]!.GetValue<string>(),
            root["generation"]!.GetValue<long>(),
            contentDigest,
            AuthenticationTag);
    }

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
        string request,
        string operation,
        string requestHash,
        HumanInputRequestPersistenceBoundary? boundary = null,
        long generation = 0,
        string? relatedRequestId = null)
    {
        return Verification.CancellationHostProcess.Start(
            "human-input-request-store",
            mode,
            workspace,
            trustRoot,
            gate,
            ready,
            output,
            request,
            operation,
            requestHash,
            boundary?.ToString() ?? string.Empty,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            relatedRequestId ?? string.Empty);
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(15), $"Cross-process Human Input store host did not publish `{path}`.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertProcessSucceededAsync(Process process)
    {
        var error = await process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error + Environment.NewLine + output);
    }

}
