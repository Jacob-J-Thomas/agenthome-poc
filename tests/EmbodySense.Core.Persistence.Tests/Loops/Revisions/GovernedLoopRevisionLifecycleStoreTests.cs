using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Loops.Revisions.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Revisions;

public sealed class GovernedLoopRevisionLifecycleStoreTests
{
    private const string CrossProcessMode = "EMBODYSENSE_REVISION_STORE_MODE";
    private const string CrossProcessWorkspace = "EMBODYSENSE_REVISION_STORE_WORKSPACE";
    private const string CrossProcessTrustRoot = "EMBODYSENSE_REVISION_STORE_TRUST_ROOT";
    private const string CrossProcessGate = "EMBODYSENSE_REVISION_STORE_GATE";
    private const string CrossProcessReady = "EMBODYSENSE_REVISION_STORE_READY";
    private const string CrossProcessOutput = "EMBODYSENSE_REVISION_STORE_OUTPUT";
    private const string CrossProcessGraph = "EMBODYSENSE_REVISION_STORE_GRAPH";
    private const string CrossProcessRevision = "EMBODYSENSE_REVISION_STORE_REVISION";
    private const string CrossProcessOperation = "EMBODYSENSE_REVISION_STORE_OPERATION";
    private const string CrossProcessRequestHash = "EMBODYSENSE_REVISION_STORE_REQUEST_HASH";
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset _time = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Commit_restart_read_and_exact_replay_preserve_one_immutable_graph_aggregate()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);

        var committed = await Store(paths, trust).CommitAsync(mutation);
        var restarted = Store(paths, trust);
        var read = await restarted.ReadGraphAsync("graph-one");
        var mutationRead = await restarted.ReadForMutationAsync("graph-one", "create-one", HashA);
        var replayed = await restarted.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(1, committed.StoreGeneration);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, read.Status);
        Assert.Equal(1, read.StoreGeneration);
        Assert.Equal(mutation.HeadToWrite, read.Snapshot!.Head);
        Assert.Equal(mutation.ArtifactToAppend, Assert.Single(read.Snapshot.Artifacts));
        Assert.Equal(mutation.Operation, Assert.Single(read.Snapshot.Operations));
        Assert.Equal(mutation.Operation, mutationRead.ExistingOperation!.Evidence);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(committed.StoreGeneration, replayed.StoreGeneration);
    }

    [Fact]
    public async Task Changed_operation_intent_conflicts_globally_and_stale_generation_cannot_append()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateDraftMutation("graph-one", "revision-one", "shared-operation", HashA, 0);
        var otherGraph = CreateDraftMutation("graph-two", "revision-two", "shared-operation", HashB, 1);
        var replacement = ReplaceDraftMutation(created, "revision-next", "replace-one", HashB, 0);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        var reused = await store.CommitAsync(otherGraph);
        var stale = await store.CommitAsync(replacement);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.OperationConflict, reused.Status);
        Assert.Equal("graph-one", reused.Operation!.GraphId);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.StoreConflict, stale.Status);
        Assert.Single((await store.ReadGraphAsync("graph-one")).Snapshot!.Artifacts);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, (await store.ReadGraphAsync("graph-two")).Status);
    }

    [Fact]
    public async Task Concurrent_store_instances_serialize_one_exact_generation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var first = Store(paths, trust);
        var second = Store(paths, trust);
        var commits = new[]
        {
            first.CommitAsync(CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0)),
            second.CommitAsync(CreateDraftMutation("graph-two", "revision-two", "create-two", HashB, 0))
        };

        var outcomes = await Task.WhenAll(commits);

        Assert.Single(outcomes, outcome => outcome.Status == GovernedLoopRevisionStoreCommitStatus.Committed);
        Assert.Single(outcomes, outcome => outcome.Status == GovernedLoopRevisionStoreCommitStatus.StoreConflict);
    }

    [Fact]
    public async Task Failure_before_primary_publication_is_unavailable_and_exact_retry_commits_normally()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var interrupted = Store(paths, trust, FailAt(GovernedLoopRevisionPersistenceBoundary.ProofPublished));

        var first = await interrupted.CommitAsync(mutation);
        var retry = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, first.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, retry.Status);
        Assert.Single((await Store(paths, trust).ReadGraphAsync("graph-one")).Snapshot!.Artifacts);
    }

    [Fact]
    public async Task Published_direct_successor_is_finalized_only_by_exact_graph_operation_and_request_hash_retry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var interrupted = Store(paths, trust, FailAt(GovernedLoopRevisionPersistenceBoundary.PrimaryPublished));

        var first = await interrupted.CommitAsync(mutation);
        var unrelatedGraph = await Store(paths, trust).ReadForMutationAsync("graph-two", "create-one", HashA);
        var changedRequest = await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashB);
        var exact = await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashA);
        var replayed = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, first.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, unrelatedGraph.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, changedRequest.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, exact.Status);
        Assert.Equal(mutation.Operation, exact.ExistingOperation!.Evidence);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, replayed.Status);
    }

    [Fact]
    public async Task Failure_after_trust_advance_is_ambiguous_then_restart_replays_the_proved_outcome()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);

        var first = await Store(paths, trust, FailAt(GovernedLoopRevisionPersistenceBoundary.TrustAdvanced)).CommitAsync(mutation);
        var restarted = Store(paths, trust);
        var read = await restarted.ReadForMutationAsync("graph-one", "create-one", HashA);
        var replayed = await restarted.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, first.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, read.Status);
        Assert.Equal(mutation.Operation, read.ExistingOperation!.Evidence);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, replayed.Status);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("workspace")]
    [InlineData("current-generation")]
    [InlineData("current-digest")]
    [InlineData("previous-generation")]
    [InlineData("previous-digest")]
    [InlineData("null")]
    public async Task Direct_commit_does_not_acknowledge_or_observe_a_non_exact_trust_successor(string substitution)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var observed = new List<GovernedLoopRevisionPersistenceBoundary>();
        var options = ObserveBoundaries(observed);
        var substituting = SubstituteAdvanceResult(trust, substitution);
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);

        var result = await Store(paths, substituting, options).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, result.Status);
        Assert.Equal(0, result.StoreGeneration);
        Assert.Null(result.Operation);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(GovernedLoopRevisionPersistenceBoundary.TrustAdvanced, observed);
        var recovered = await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashA);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, recovered.Status);
        Assert.Equal(mutation.Operation, recovered.ExistingOperation!.Evidence);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("workspace")]
    [InlineData("current-generation")]
    [InlineData("current-digest")]
    [InlineData("previous-generation")]
    [InlineData("previous-digest")]
    [InlineData("null")]
    public async Task Pending_recovery_does_not_expose_or_observe_a_non_exact_trust_successor(string substitution)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Ambiguous,
            (await Store(paths, trust, FailAt(GovernedLoopRevisionPersistenceBoundary.PrimaryPublished)).CommitAsync(mutation)).Status);
        var observed = new List<GovernedLoopRevisionPersistenceBoundary>();
        var options = ObserveBoundaries(observed);
        var substituting = SubstituteAdvanceResult(trust, substitution);

        var result = await Store(paths, substituting, options).ReadForMutationAsync("graph-one", "create-one", HashA);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, result.Status);
        Assert.Equal(0, result.StoreGeneration);
        Assert.Null(result.ExistingOperation);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(GovernedLoopRevisionPersistenceBoundary.TrustAdvanced, observed);
        var recovered = await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashA);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, recovered.Status);
        Assert.Equal(mutation.Operation, recovered.ExistingOperation!.Evidence);
    }

    [Fact]
    public async Task Replacement_and_publication_form_one_append_only_lineage_and_operation_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var replaced = ReplaceDraftMutation(created, "revision-two", "replace-one", HashB, 1);
        var published = PublishMutation(replaced, "publish-one", HashC, 2);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(replaced)).Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(published)).Status);
        var snapshot = (await store.ReadGraphAsync("graph-one")).Snapshot!;

        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Published, snapshot.Head.Status);
        Assert.Equal(3, snapshot.Head.LifecycleVersion);
        Assert.Equal(2, snapshot.Artifacts.Count);
        Assert.Equal(3, snapshot.Operations.Count);
        Assert.Equal(snapshot.Artifacts[0].Revision, snapshot.Artifacts[1].PredecessorRevision);
        Assert.Equal(published.Operation.OperationId, snapshot.Head.PublishedRevision!.PublicationOperationId);
    }

    [Fact]
    public async Task Unknown_or_duplicate_json_recovers_only_last_proved_state_and_never_mutates_from_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var replaced = ReplaceDraftMutation(created, "revision-two", "replace-one", HashB, 1);
        await store.CommitAsync(created);
        await store.CommitAsync(replaced);
        var primaryPath = PrimaryPath(paths);
        var original = await File.ReadAllTextAsync(primaryPath);

        var unknown = JsonNode.Parse(original)!.AsObject();
        unknown["authority"] = "self-granted";
        await File.WriteAllTextAsync(primaryPath, unknown.ToJsonString());
        var recovered = await Store(paths, trust).ReadGraphAsync("graph-one");
        var rejected = await Store(paths, trust).CommitAsync(PublishMutation(replaced, "publish-after-corruption", HashC, 2));
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, recovered.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, rejected.Status);

        await File.WriteAllTextAsync(primaryPath, original.Replace("\"generation\": 2", "\"generation\": 2, \"generation\": 2", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, (await Store(paths, trust).ReadGraphAsync("graph-one")).Status);
    }

    [Fact]
    public async Task Configured_no_eviction_quota_rejects_a_second_artifact_without_rewriting_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var options = new GovernedLoopRevisionStoreOptions { MaxRevisionArtifacts = 1 };
        var store = Store(paths, trust, options);
        var created = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var replaced = ReplaceDraftMutation(created, "revision-two", "replace-one", HashB, 1);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, (await store.CommitAsync(replaced)).Status);
        var snapshot = (await store.ReadGraphAsync("graph-one")).Snapshot!;
        Assert.Equal(created.ArtifactToAppend, Assert.Single(snapshot.Artifacts));
        Assert.Equal(created.Operation, Assert.Single(snapshot.Operations));
    }

    [Fact]
    public async Task Primary_rename_durability_failure_is_ambiguous_and_direct_commit_retry_finalizes_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var interrupted = new GovernedLoopRevisionLifecycleStore(
            paths,
            trust,
            durabilityBarrier: new FailAfterPrimaryRenameBarrier());

        var first = await interrupted.CommitAsync(mutation);
        var recovered = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, first.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, recovered.Status);
        Assert.Equal(mutation.Operation, recovered.Operation!.Evidence);
    }

    [Fact]
    public async Task Exact_pending_finalize_cancellation_remains_ambiguous_without_advancing_changed_intent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        await Store(paths, trust, FailAt(GovernedLoopRevisionPersistenceBoundary.PrimaryPublished)).CommitAsync(mutation);
        var canceling = new GovernedLoopRevisionLifecycleStore(paths, new CancelingAdvanceTrustProvider(trust));

        var result = await canceling.CommitAsync(mutation);
        var changed = await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashB);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, result.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, changed.Status);
    }

    [Fact]
    public async Task Pre_canceled_commit_propagates_before_authority_entry_without_publishing_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths, new TestCapabilityLifecycleTrustProvider());
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(mutation, new CancellationToken(canceled: true)));

        Assert.False(File.Exists(PrimaryPath(paths)));
        Assert.False(File.Exists(Path.Combine(paths.AgentPath, "loops", "revisions", "lifecycle.proved.json")));
    }

    [Fact]
    public async Task Caller_cancellation_during_existing_generation_trust_read_propagates_without_publishing_intent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var created = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var replacement = ReplaceDraftMutation(created, "revision-two", "replace-one", HashB, 1);
        var store = Store(paths, trust);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        using var cancellation = new CancellationTokenSource();
        trust.BeforeRead = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(replacement, cancellation.Token));

        trust.BeforeRead = null;
        var read = await Store(paths, trust).ReadGraphAsync("graph-one");
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, read.Status);
        Assert.Equal(1, read.StoreGeneration);
        Assert.Equal(created.HeadToWrite, read.Snapshot!.Head);
        Assert.Equal(created.Operation, Assert.Single(read.Snapshot.Operations));
    }

    [Fact]
    public async Task Caller_cancellation_during_mutation_read_propagates_before_pending_recovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var store = Store(paths, trust);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(mutation)).Status);
        using var cancellation = new CancellationTokenSource();
        trust.BeforeRead = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReadForMutationAsync("graph-one", "create-one", HashA, cancellation.Token));

        trust.BeforeRead = null;
        var read = await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashA);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, read.Status);
        Assert.Equal(mutation.Operation, read.ExistingOperation!.Evidence);
        Assert.Equal(mutation.HeadToWrite, read.Snapshot!.Head);
    }

    [Fact]
    public async Task Mutation_read_availability_failure_before_pending_recovery_is_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var store = Store(paths, trust);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(mutation)).Status);
        trust.BeforeRead = _ => throw new IOException("Injected trust availability failure.");

        var unavailable = await store.ReadForMutationAsync("graph-one", "create-one", HashA);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Unavailable, unavailable.Status);
        Assert.Equal(0, unavailable.StoreGeneration);
        Assert.Null(unavailable.ExistingOperation);
        Assert.Null(unavailable.Snapshot);
        trust.BeforeRead = null;
        Assert.Equal(
            GovernedLoopRevisionStoreReadStatus.Ready,
            (await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashA)).Status);
    }

    [Fact]
    public async Task Caller_cancellation_during_exact_pending_read_recovery_is_ambiguous_and_retry_recovers()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Ambiguous,
            (await Store(paths, trust, FailAt(GovernedLoopRevisionPersistenceBoundary.PrimaryPublished)).CommitAsync(mutation)).Status);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new GovernedLoopRevisionLifecycleStore(
            paths,
            new CancelingAdvanceTrustProvider(trust, cancellation.Cancel));

        var ambiguous = await interrupted.ReadForMutationAsync(
            "graph-one",
            "create-one",
            HashA,
            cancellation.Token);
        var replay = await Store(paths, trust).ReadForMutationAsync("graph-one", "create-one", HashA);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, ambiguous.Status);
        Assert.Null(ambiguous.ExistingOperation);
        Assert.Null(ambiguous.Snapshot);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, replay.Status);
        Assert.Equal(mutation.Operation, replay.ExistingOperation!.Evidence);
        Assert.Equal(mutation.HeadToWrite, replay.Snapshot!.Head);
    }

    [Fact]
    public async Task Caller_cancellation_after_primary_publication_is_ambiguous_and_exact_retry_recovers()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        using var cancellation = new CancellationTokenSource();
        var options = new GovernedLoopRevisionStoreOptions
        {
            DurableBoundaryObserver = (boundary, token) =>
            {
                if (boundary == GovernedLoopRevisionPersistenceBoundary.PrimaryPublished)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return ValueTask.CompletedTask;
            },
        };

        var interrupted = await Store(paths, trust, options).CommitAsync(mutation, cancellation.Token);
        var replayed = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(mutation.Operation, replayed.Operation!.Evidence);
        Assert.Equal(mutation.HeadToWrite, replayed.Snapshot!.Head);
    }

    [Fact]
    public async Task Outer_authority_release_failure_preserves_the_completed_commit_result()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var transaction = new ThrowAfterCallbackAuthorityTransaction();
        var store = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);

        var result = await store.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.StoreGeneration);
    }

    [Fact]
    public async Task Outer_authority_release_failure_preserves_exact_pending_finalize_read_result()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var interrupted = Store(paths, trust, FailAt(GovernedLoopRevisionPersistenceBoundary.PrimaryPublished));
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, (await interrupted.CommitAsync(mutation)).Status);
        var transaction = new ThrowAfterCallbackAuthorityTransaction();
        var releasing = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);

        var result = await releasing.ReadForMutationAsync("graph-one", "create-one", HashA);
        var restarted = await Store(paths, trust).ReadGraphAsync("graph-one");

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, result.Status);
        Assert.Equal(mutation.Operation, result.ExistingOperation!.Evidence);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, restarted.Status);
        Assert.Equal(mutation.HeadToWrite, restarted.Snapshot!.Head);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outer_authority_release_failure_preserves_completed_graph_read_result(bool cancelRelease)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await Store(paths, trust).CommitAsync(mutation)).Status);
        ICapabilityAuthorityTransaction transaction = cancelRelease
            ? new CancelAfterCallbackAuthorityTransaction()
            : new ThrowAfterCallbackAuthorityTransaction();
        var releasing = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);

        var result = await releasing.ReadGraphAsync("graph-one");

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, result.Status);
        Assert.Equal(mutation.HeadToWrite, result.Snapshot!.Head);
    }

    [Fact]
    public async Task Json_property_casing_and_quoted_numbers_are_rejected_strictly()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        await Store(paths, trust).CommitAsync(CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0));
        var primaryPath = PrimaryPath(paths);
        var original = await File.ReadAllTextAsync(primaryPath);

        await File.WriteAllTextAsync(primaryPath, original.Replace("\"generation\": 1", "\"Generation\": 1", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, (await Store(paths, trust).ReadGraphAsync("graph-one")).Status);

        await File.WriteAllTextAsync(primaryPath, original.Replace("\"generation\": 1", "\"generation\": \"1\"", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, (await Store(paths, trust).ReadGraphAsync("graph-one")).Status);
    }

    [Theory]
    [InlineData("not-object")]
    [InlineData("unknown-property")]
    [InlineData("missing-property")]
    [InlineData("schema-type")]
    [InlineData("graph-type")]
    [InlineData("revision-type")]
    [InlineData("hash-type")]
    [InlineData("invalid-value")]
    [InlineData("case-changed")]
    [InlineData("duplicate-property")]
    public async Task Malformed_revision_reference_json_is_rejected_without_partial_rehydration(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        await Store(paths, trust).CommitAsync(CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0));
        var primaryPath = PrimaryPath(paths);
        var original = await File.ReadAllTextAsync(primaryPath);
        string corrupted;
        if (corruption == "duplicate-property")
        {
            corrupted = original.Replace(
                "\"graphId\": \"graph-one\"",
                "\"graphId\": \"graph-one\", \"graphId\": \"graph-one\"",
                StringComparison.Ordinal);
        }
        else
        {
            var root = JsonNode.Parse(original)!.AsObject();
            var artifact = root["artifacts"]!.AsArray()[0]!.AsObject();
            var revision = artifact["revision"]!.AsObject();
            switch (corruption)
            {
                case "not-object":
                    artifact["revision"] = "invalid";
                    break;
                case "unknown-property":
                    revision["authority"] = "ambient";
                    break;
                case "missing-property":
                    revision.Remove("executableHash");
                    break;
                case "schema-type":
                    revision["schemaVersion"] = "1";
                    break;
                case "graph-type":
                    revision["graphId"] = 1;
                    break;
                case "revision-type":
                    revision["revisionId"] = 1;
                    break;
                case "hash-type":
                    revision["executableHash"] = 1;
                    break;
                case "invalid-value":
                    revision["graphId"] = "UPPERCASE";
                    break;
                case "case-changed":
                    revision["GraphId"] = revision["graphId"]!.DeepClone();
                    revision.Remove("graphId");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption));
            }

            corrupted = root.ToJsonString();
        }

        await File.WriteAllTextAsync(primaryPath, corrupted);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, (await Store(paths, trust).ReadGraphAsync("graph-one")).Status);
    }

    [Theory]
    [InlineData("status-case")]
    [InlineData("kind-case")]
    [InlineData("outcome-case")]
    [InlineData("integer-kind")]
    public async Task Authenticated_noncanonical_enum_tokens_are_rejected_without_fallback_rehydration(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        await Store(paths, trust).CommitAsync(CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0));
        var primaryPath = PrimaryPath(paths);
        var original = await File.ReadAllTextAsync(primaryPath);
        var corrupted = corruption switch
        {
            "status-case" => original.Replace("\"status\": \"draft\"", "\"status\": \"DRAFT\"", StringComparison.Ordinal),
            "kind-case" => original.Replace("\"kind\": \"create-draft\"", "\"kind\": \"Create-Draft\"", StringComparison.Ordinal),
            "outcome-case" => original.Replace("\"outcome\": \"committed\"", "\"outcome\": \"Committed\"", StringComparison.Ordinal),
            "integer-kind" => original.Replace("\"kind\": \"create-draft\"", "\"kind\": 1", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        Assert.NotEqual(original, corrupted);
        await File.WriteAllTextAsync(primaryPath, corrupted);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, (await Store(paths, trust).ReadGraphAsync("graph-one")).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Authenticated_generation_must_equal_the_append_only_operation_count(long generation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var created = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var replaced = ReplaceDraftMutation(created, "revision-two", "replace-one", HashB, 1);
        var store = Store(paths, trust);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(replaced)).Status);

        var pinnedTrust = await RewriteAuthenticatedGenerationAsync(paths, generation);

        var read = await Store(paths, pinnedTrust).ReadGraphAsync("graph-one");
        var mutationRead = await Store(paths, pinnedTrust).ReadForMutationAsync("graph-one", "replace-one", HashB);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Unavailable, read.Status);
        Assert.Null(read.Snapshot);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Unavailable, mutationRead.Status);
        Assert.Null(mutationRead.ExistingOperation);
    }

    [Fact]
    public async Task Copied_workspace_artifacts_do_not_inherit_server_trust()
    {
        using var source = new TestWorkspace();
        using var destination = new TestWorkspace();
        var trust = new TestCapabilityLifecycleTrustProvider();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var destinationPaths = new WorkspacePaths(destination.RootPath);
        await Store(sourcePaths, trust).CommitAsync(CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0));
        CopyDirectory(sourcePaths.AgentPath, destinationPaths.AgentPath);

        var copied = await Store(destinationPaths, trust).ReadGraphAsync("graph-one");

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Unavailable, copied.Status);
        Assert.Null(copied.Snapshot);
    }

    [Fact]
    public async Task Symlinked_revision_directory_fails_closed_without_publishing_artifacts()
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
            .CommitAsync(CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0));

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Constructor_rejects_null_inputs_impossible_limits_and_overlapping_trust_topology()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopRevisionLifecycleStore(null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopRevisionLifecycleStore(paths, (ICapabilityCatalogTrustProvider)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopRevisionLifecycleStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopRevisionStoreOptions { MaxGraphHeads = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopRevisionLifecycleStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(0)));
        Assert.Throws<InvalidOperationException>(() => new GovernedLoopRevisionLifecycleStore(
            paths,
            new FileCapabilityCatalogTrustProvider(Path.Combine(paths.AgentPath, "server-trust"))));
    }

    [Fact]
    public async Task Cross_process_writers_have_one_exact_generation_winner()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = Path.Combine(workspace.RootPath, "release-revision-writers");
        var firstReady = Path.Combine(workspace.RootPath, "first-revision-ready");
        var secondReady = Path.Combine(workspace.RootPath, "second-revision-ready");
        var firstOutput = Path.Combine(workspace.RootPath, "first-revision-result");
        var secondOutput = Path.Combine(workspace.RootPath, "second-revision-result");
        using var first = StartCrossProcessHost(
            "writer",
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            firstReady,
            firstOutput,
            "graph-one",
            "revision-one",
            "create-one",
            HashA);
        using var second = StartCrossProcessHost(
            "writer",
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            secondReady,
            secondOutput,
            "graph-two",
            "revision-two",
            "create-two",
            HashB);

        await Task.WhenAll(WaitForPathAsync(firstReady), WaitForPathAsync(secondReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProcessSucceededAsync(first);
        await AssertProcessSucceededAsync(second);
        var statuses = new[] { await File.ReadAllTextAsync(firstOutput), await File.ReadAllTextAsync(secondOutput) };

        Assert.Single(statuses, status => status == GovernedLoopRevisionStoreCommitStatus.Committed.ToString());
        Assert.Single(statuses, status => status == GovernedLoopRevisionStoreCommitStatus.StoreConflict.ToString());
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var committedGraphs = await Task.WhenAll(
            new GovernedLoopRevisionLifecycleStore(new WorkspacePaths(workspace.RootPath), provider).ReadGraphAsync("graph-one"),
            new GovernedLoopRevisionLifecycleStore(new WorkspacePaths(workspace.RootPath), provider).ReadGraphAsync("graph-two"));
        Assert.Single(committedGraphs, read => read.Status == GovernedLoopRevisionStoreReadStatus.Ready);
        Assert.Single(committedGraphs, read => read.Status == GovernedLoopRevisionStoreReadStatus.NotFound);
    }

    [Fact]
    public async Task Abrupt_process_loss_after_primary_publication_recovers_by_exact_retry()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = Path.Combine(workspace.RootPath, "release-revision-crash");
        var ready = Path.Combine(workspace.RootPath, "revision-crash-ready");
        var output = Path.Combine(workspace.RootPath, "revision-crash-result");
        using var process = StartCrossProcessHost(
            "crash-primary",
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            ready,
            output,
            "graph-one",
            "revision-one",
            "create-one",
            HashA);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, process.ExitCode);
        Assert.False(File.Exists(output));
        var mutation = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var restarted = new GovernedLoopRevisionLifecycleStore(
            new WorkspacePaths(workspace.RootPath),
            new FileCapabilityCatalogTrustProvider(trustRoot.RootPath));
        var recovered = await restarted.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, recovered.Status);
        Assert.Equal(mutation.Operation, recovered.Operation!.Evidence);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, (await restarted.ReadGraphAsync("graph-one")).Status);
    }

    [Fact]
    public async Task Missing_lifecycle_not_found_receipt_retains_the_requested_target_without_fabricating_an_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var target = Revision("graph-one", "missing-revision", HashA);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            "publish-missing-lifecycle",
            "actor-one",
            HashA,
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.NotFound,
            GovernedLoopRevisionOperationFailureCode.LifecycleNotFound,
            null,
            null,
            null,
            target,
            null,
            HashB,
            null,
            _time);
        var mutation = new GovernedLoopRevisionStoreMutation("graph-one", 0, operation, null, null);

        var committed = await Store(paths, trust).CommitAsync(mutation);
        var read = await Store(paths, trust).ReadForMutationAsync("graph-one", operation.OperationId, operation.RequestHash);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.Null(committed.Snapshot);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, read.Status);
        Assert.Equal(operation, read.ExistingOperation!.Evidence);
    }

    [Fact]
    public async Task Rollback_publication_not_found_receipt_preserves_unproved_requested_source_and_current_head()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        var published = PublishMutation(created, "publish-one", HashB, 1);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(published)).Status);
        var sourceRevision = Revision("graph-one", "missing-source", HashB);
        var source = GovernedLoopRevisionPublicationPinFactory.Create(1, sourceRevision, "missing-publication", HashC);
        var candidate = Revision("graph-one", "rollback-successor", HashB);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            "rollback-missing-publication",
            "actor-one",
            HashC,
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.NotFound,
            GovernedLoopRevisionOperationFailureCode.PublicationNotFound,
            published.HeadToWrite,
            published.HeadToWrite,
            candidate,
            published.HeadToWrite!.PublishedRevision!.Revision,
            source,
            HashB,
            null,
            _time.AddMinutes(3));
        var mutation = new GovernedLoopRevisionStoreMutation("graph-one", 2, operation, null, null);

        var committed = await store.CommitAsync(mutation);
        var snapshot = (await store.ReadGraphAsync("graph-one")).Snapshot!;

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(published.HeadToWrite, snapshot.Head);
        Assert.Equal(operation, snapshot.Operations[^1]);
        Assert.Single(snapshot.Artifacts);
    }

    [Fact]
    public async Task Lifecycle_version_limit_receipt_is_accepted_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var created = CreateDraftMutation("graph-one", "revision-one", "create-one", HashA, 0);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);
        var currentHead = created.HeadToWrite!;
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            "publish-at-version-limit",
            "actor-one",
            HashB,
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.LimitExceeded,
            GovernedLoopRevisionOperationFailureCode.LifecycleVersionLimitExceeded,
            currentHead,
            currentHead,
            null,
            currentHead.DraftRevision,
            null,
            HashC,
            null,
            _time.AddMinutes(1));
        var mutation = new GovernedLoopRevisionStoreMutation("graph-one", 1, operation, null, null);

        var committed = await store.CommitAsync(mutation);
        var restarted = Store(paths, trust);
        var read = await restarted.ReadForMutationAsync("graph-one", operation.OperationId, operation.RequestHash);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(operation, read.ExistingOperation!.Evidence);
        Assert.Equal(operation, (await restarted.ReadGraphAsync("graph-one")).Snapshot!.Operations[^1]);
    }

    [Fact]
    public async Task Cross_process_revision_store_host()
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
        var graph = Environment.GetEnvironmentVariable(CrossProcessGraph)!;
        var revision = Environment.GetEnvironmentVariable(CrossProcessRevision)!;
        var operation = Environment.GetEnvironmentVariable(CrossProcessOperation)!;
        var requestHash = Environment.GetEnvironmentVariable(CrossProcessRequestHash)!;
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForPathAsync(gate);
        GovernedLoopRevisionStoreOptions? options = mode == "crash-primary"
            ? new GovernedLoopRevisionStoreOptions
            {
                DurableBoundaryObserver = (boundary, _) =>
                {
                    if (boundary == GovernedLoopRevisionPersistenceBoundary.PrimaryPublished)
                    {
                        TerminateCrossProcessHost();
                    }

                    return ValueTask.CompletedTask;
                }
            }
            : null;
        var store = new GovernedLoopRevisionLifecycleStore(
            new WorkspacePaths(workspace),
            new FileCapabilityCatalogTrustProvider(trustRoot),
            options);
        var mutation = CreateDraftMutation(graph, revision, operation, requestHash, 0);
        var retryWindow = Stopwatch.StartNew();
        GovernedLoopRevisionStoreCommitResult result;
        do
        {
            result = await store.CommitAsync(mutation);
            if (mode != "writer"
                || result.Status != GovernedLoopRevisionStoreCommitStatus.Unavailable
                || retryWindow.Elapsed >= TimeSpan.FromSeconds(15))
            {
                break;
            }

            await Task.Delay(50);
        }
        while (true);

        await File.WriteAllTextAsync(output, result.Status.ToString());
    }

    private static GovernedLoopRevisionLifecycleStore Store(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trust,
        GovernedLoopRevisionStoreOptions? options = null)
        => new(paths, trust, options);

    private static GovernedLoopRevisionStoreOptions FailAt(GovernedLoopRevisionPersistenceBoundary target)
        => new()
        {
            DurableBoundaryObserver = (boundary, _) => boundary == target
                ? ValueTask.FromException(new IOException("Injected durable-boundary interruption."))
                : ValueTask.CompletedTask
        };

    private static GovernedLoopRevisionStoreOptions ObserveBoundaries(ICollection<GovernedLoopRevisionPersistenceBoundary> observed)
        => new()
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                observed.Add(boundary);
                return ValueTask.CompletedTask;
            }
        };

    private static ICapabilityCatalogTrustProvider SubstituteAdvanceResult(
        ICapabilityCatalogTrustProvider trust,
        string substitution)
    {
        return new SubstitutingAdvanceTrustProvider(
            trust,
            advanceInner: substitution != "stale",
            advanced => substitution switch
            {
                "stale" => advanced,
                "workspace" => advanced with { WorkspaceIdentity = advanced.WorkspaceIdentity + "-substituted" },
                "current-generation" => advanced with { CurrentGeneration = checked(advanced.CurrentGeneration + 1) },
                "current-digest" => advanced with { CurrentContentDigest = "not-the-candidate-digest" },
                "previous-generation" => advanced with { PreviousGeneration = checked(advanced.PreviousGeneration!.Value + 1) },
                "previous-digest" => advanced with { PreviousContentDigest = "not-the-previous-digest" },
                "null" => null!,
                _ => throw new ArgumentOutOfRangeException(nameof(substitution))
            });
    }

    private static GovernedLoopRevisionStoreMutation CreateDraftMutation(
        string graphId,
        string revisionId,
        string operationId,
        string requestHash,
        long generation)
    {
        var revision = Revision(graphId, revisionId, HashA);
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            graphId,
            1,
            GovernedLoopRevisionLifecycleStatus.Draft,
            revision,
            null,
            operationId,
            _time);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(1, revision, null, null, operationId, "actor-one", _time);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            requestHash,
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            null,
            head,
            revision,
            null,
            null,
            HashB,
            null,
            _time);
        return new GovernedLoopRevisionStoreMutation(graphId, generation, operation, artifact, head);
    }

    private static GovernedLoopRevisionStoreMutation ReplaceDraftMutation(
        GovernedLoopRevisionStoreMutation previous,
        string revisionId,
        string operationId,
        string requestHash,
        long generation)
    {
        var previousHead = previous.HeadToWrite!;
        var previousRevision = previousHead.DraftRevision!;
        var revision = Revision(previous.GraphId, revisionId, HashB);
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            previous.GraphId,
            previousHead.LifecycleVersion + 1,
            previousHead.Status,
            revision,
            previousHead.PublishedRevision,
            operationId,
            _time.AddMinutes(1));
        var artifact = GovernedLoopRevisionArtifactFactory.Create(
            1,
            revision,
            previousRevision,
            null,
            operationId,
            "actor-one",
            _time.AddMinutes(1));
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            requestHash,
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previousHead,
            head,
            revision,
            previousRevision,
            null,
            HashB,
            null,
            _time.AddMinutes(1));
        return new GovernedLoopRevisionStoreMutation(previous.GraphId, generation, operation, artifact, head);
    }

    private static GovernedLoopRevisionStoreMutation PublishMutation(
        GovernedLoopRevisionStoreMutation previous,
        string operationId,
        string requestHash,
        long generation)
    {
        var previousHead = previous.HeadToWrite!;
        var target = previousHead.DraftRevision!;
        var pin = GovernedLoopRevisionPublicationPinFactory.Create(1, target, operationId, HashC);
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            previous.GraphId,
            previousHead.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Published,
            null,
            pin,
            operationId,
            _time.AddMinutes(2));
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            requestHash,
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previousHead,
            head,
            null,
            target,
            null,
            HashB,
            HashC,
            _time.AddMinutes(2));
        return new GovernedLoopRevisionStoreMutation(previous.GraphId, generation, operation, null, head);
    }

    private static GovernedLoopRevisionReference Revision(string graphId, string revisionId, string executableHash)
        => GovernedLoopRevisionReference.Create(1, graphId, revisionId, executableHash);

    private static string PrimaryPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "revisions", "lifecycle.json");

    private static async Task<ICapabilityCatalogTrustProvider> RewriteAuthenticatedGenerationAsync(
        WorkspacePaths paths,
        long generation)
    {
        var primaryPath = PrimaryPath(paths);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(primaryPath))!.AsObject();
        root["generation"] = generation;
        root["contentDigest"] = string.Empty;
        root["authenticationTag"] = string.Empty;
        var canonical = root.ToJsonString();
        var contentDigest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(canonical)).Value;
        const string AuthenticationTag = "pinned-authenticated-generation";
        root["contentDigest"] = contentDigest;
        root["authenticationTag"] = AuthenticationTag;
        await File.WriteAllTextAsync(primaryPath, root.ToJsonString() + Environment.NewLine);
        return new PinnedDocumentTrustProvider(
            root["workspaceIdentity"]!.GetValue<string>(),
            generation,
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
        string graph,
        string revision,
        string operation,
        string requestHash)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        EmbodySense.Core.Persistence.Tests.Verification.CoverageChildProcessAssembly.AddVstestArguments(
            startInfo,
            typeof(GovernedLoopRevisionLifecycleStoreTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Loops.Revisions.GovernedLoopRevisionLifecycleStoreTests.Cross_process_revision_store_host");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessMode] = mode;
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessTrustRoot] = trustRoot;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOutput] = output;
        startInfo.Environment[CrossProcessGraph] = graph;
        startInfo.Environment[CrossProcessRevision] = revision;
        startInfo.Environment[CrossProcessOperation] = operation;
        startInfo.Environment[CrossProcessRequestHash] = requestHash;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process revision-store test host did not start.");
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(15), $"Cross-process revision-store host did not publish `{path}`.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertProcessSucceededAsync(Process process)
    {
        var error = await process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error + Environment.NewLine + output);
    }

    private static void TerminateCrossProcessHost()
    {
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }

    private sealed class FailAfterPrimaryRenameBarrier : ICapabilityCatalogDurabilityBarrier
    {
        public void BeforeDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void AfterDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void FlushAfterDirectoryCreate(string directoryPath, Microsoft.Win32.SafeHandles.SafeFileHandle parentDirectory)
        {
        }

        public ValueTask FlushAfterRenameAsync(string destinationPath, Microsoft.Win32.SafeHandles.SafeFileHandle parentDirectory)
            => Path.GetFileName(destinationPath) == "lifecycle.json"
                ? ValueTask.FromException(new IOException("Injected failure after the primary rename."))
                : ValueTask.CompletedTask;
    }

    private sealed class CancelingAdvanceTrustProvider(ICapabilityCatalogTrustProvider inner, Action? beforeAdvance = null) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

        public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
            => inner.ReadAsync(workspaceIdentity, cancellationToken);

        public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
            => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
            => inner.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default)
            => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

        public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default)
        {
            beforeAdvance?.Invoke();
            return beforeAdvance is null
                ? Task.FromCanceled<CapabilityCatalogTrustState>(new CancellationToken(canceled: true))
                : inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
        }
    }

    private sealed class PinnedDocumentTrustProvider(
        string expectedWorkspaceIdentity,
        long generation,
        string contentDigest,
        string authenticationTag) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => 128;

        public void RequireDisjointWorkspace(string workspaceRootPath)
        {
        }

        public Task<CapabilityCatalogTrustState?> ReadAsync(
            string workspaceIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapabilityCatalogTrustState? state = string.Equals(workspaceIdentity, expectedWorkspaceIdentity, StringComparison.Ordinal)
                ? new CapabilityCatalogTrustState(workspaceIdentity, generation, contentDigest, null, null)
                : null;
            return Task.FromResult(state);
        }

        public Task<CapabilityCatalogTrustState> InitializeAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> AuthenticateArtifactAsync(
            string workspaceIdentity,
            long generation,
            string contentDigest,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> VerifyArtifactAsync(
            string workspaceIdentity,
            long candidateGeneration,
            string candidateContentDigest,
            string candidateAuthenticationTag,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                string.Equals(workspaceIdentity, expectedWorkspaceIdentity, StringComparison.Ordinal)
                && candidateGeneration == generation
                && string.Equals(candidateContentDigest, contentDigest, StringComparison.Ordinal)
                && string.Equals(candidateAuthenticationTag, authenticationTag, StringComparison.Ordinal));
        }

        public Task<CapabilityCatalogTrustState> AdvanceAsync(
            string workspaceIdentity,
            long expectedGeneration,
            string expectedContentDigest,
            long newGeneration,
            string newContentDigest,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowAfterCallbackAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            _ = await operation(cancellationToken);
            throw new IOException("Injected authority-release failure.");
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CancelAfterCallbackAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            _ = await operation(cancellationToken);
            throw new OperationCanceledException(new CancellationToken(canceled: true));
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
