using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.ContextualRoles.Models;
using EmbodySense.Tests.Support;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Tests.ContextualRoles;

public sealed class ContextualRoleRevisionStoreTests
{
    private static readonly DateTimeOffset _requestedAt = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Empty_and_invalid_reads_are_structured_without_initializing_role_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var missingRevision = await store.ReadAsync(new ContextualRoleRevisionReadRequest(new ContextualRoleRevisionIdentity("reviewer", 1)));
        var invalidRevision = await store.ReadAsync(new ContextualRoleRevisionReadRequest(new ContextualRoleRevisionIdentity("../unsafe", 0)));
        var missingLifecycle = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));
        var invalidLifecycle = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("../unsafe"));

        Assert.Equal(ContextualRoleRevisionReadStatus.NotFound, missingRevision.Status);
        Assert.Equal(ContextualRoleRevisionReadStatus.Invalid, invalidRevision.Status);
        Assert.Equal(ContextualRoleLifecycleReadStatus.NotFound, missingLifecycle.Status);
        Assert.Equal(ContextualRoleLifecycleReadStatus.Invalid, invalidLifecycle.Status);
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, ".agent", "contextual-roles")));
        Assert.Throws<ArgumentException>(() => new ContextualRoleRevisionStore(paths, "../unsafe"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextualRoleRevisionStore(paths, "workspace-one", new ContextualRoleRevisionStoreOptions { MaxRevisionArtifacts = ContextualRoleRevisionStoreOptions.MaximumRevisionArtifacts + 1 }));
    }

    [Fact]
    public async Task Invalid_mutation_and_initialized_store_misses_are_structured()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var invalid = await store.MutateAsync(new ContextualRoleRevisionMutationRequest("../unsafe", string.Empty, ContextualRoleRevisionMutationKind.Unknown, "../unsafe", "../unsafe", null, null, default));
        var revision = Revision("reviewer", 1);
        await store.MutateAsync(CreateRequest("create-reviewer", revision));
        var missingRevision = await store.ReadAsync(new ContextualRoleRevisionReadRequest(new ContextualRoleRevisionIdentity("reviewer", 2)));
        var missingLifecycle = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("writer"));

        Assert.Equal(ContextualRoleRevisionMutationStatus.Invalid, invalid.Status);
        Assert.NotEmpty(invalid.ValidationErrors);
        Assert.Equal(ContextualRoleRevisionReadStatus.NotFound, missingRevision.Status);
        Assert.Equal(ContextualRoleLifecycleReadStatus.NotFound, missingLifecycle.Status);
    }

    [Fact]
    public async Task Create_restart_exact_replay_and_changed_operation_reuse_are_conflict_safe()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));
        var created = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(request);
        var replay = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(request);
        var changed = ContextualRoleRevisionMutationRequestHash.Apply(request with { Revision = ContextualRoleRevisionContentHash.Apply(request.Revision! with { DisplayName = "Changed reviewer" }) });
        var conflict = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(changed);
        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(request.Revision!.Identity));

        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, created.Status);
        Assert.Equal(created.Status, replay.Status);
        Assert.Equal(created.RequestHash, replay.RequestHash);
        Assert.Equal(created.Evidence, replay.Evidence);
        AssertRevision(created.Revision!, replay.Revision);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, conflict.Status);
        Assert.Null(conflict.Evidence);
        Assert.Equal(ContextualRoleRevisionReadStatus.Found, read.Status);
        AssertRevision(request.Revision, read.Revision);
        Assert.True(created.Evidence is { Recovered: false, State: ContextualRoleLifecycleState.Active });
        Assert.Equal(request.RequestHash, created.Evidence.RequestHash);
    }

    [Fact]
    public async Task First_mutation_initializes_each_durable_artifact_stage()
    {
        using var workspace = new TestWorkspace();
        var result = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one")
            .MutateAsync(CreateRequest("create-reviewer", Revision("reviewer", 1)));
        var root = StoreRoot(workspace.RootPath);
        var revisions = Path.Combine(root, "revisions");
        var states = Path.Combine(root, "states");
        var operations = Path.Combine(root, "operations");
        var proofs = Path.Combine(root, "proofs");
        var lockPath = Path.Combine(root, ".mutations.lock");
        var anchor = Path.Combine(root, "workspace-anchor.json");
        var intent = Path.Combine(operations, "create-reviewer.intent.json");
        var revision = Path.Combine(revisions, "reviewer.1.json");
        var state = Path.Combine(states, "reviewer.json");
        var proof = Path.Combine(proofs, "create-reviewer.json");
        var terminal = Path.Combine(operations, "create-reviewer.result.json");

        Assert.True(
            result.Status == ContextualRoleRevisionMutationStatus.Accepted,
            $"status={result.Status}; root={Directory.Exists(root)}; revisions={Directory.Exists(revisions)}; states={Directory.Exists(states)}; operations={Directory.Exists(operations)}; proofs={Directory.Exists(proofs)}; lock={File.Exists(lockPath)}; anchor={File.Exists(anchor)}; intent={File.Exists(intent)}; revision={File.Exists(revision)}; state={File.Exists(state)}; proof={File.Exists(proof)}; terminal={File.Exists(terminal)}");
        Assert.All([root, revisions, states, operations, proofs], path => Assert.True(Directory.Exists(path), path));
        Assert.All([lockPath, anchor, intent, revision, state, proof, terminal], path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public async Task Replacement_preserves_history_and_stale_revision_conflict_as_immutable_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var first = Revision("reviewer", 1);
        var second = Revision("reviewer", 2, "Replacement");
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(CreateRequest("create-reviewer", first))).Status);
        var replacement = Mutation("replace-reviewer", ContextualRoleRevisionMutationKind.Replace, "reviewer", second, first.Identity);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(replacement)).Status);
        var stale = Mutation("stale-disable", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, first.Identity);
        var firstConflict = await store.MutateAsync(stale);
        var replayedConflict = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(stale);
        var oldRead = await store.ReadAsync(new ContextualRoleRevisionReadRequest(first.Identity));
        var newRead = await store.ReadAsync(new ContextualRoleRevisionReadRequest(second.Identity));
        var lifecycle = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, firstConflict.Status);
        Assert.Equal(firstConflict.Status, replayedConflict.Status);
        Assert.Equal(firstConflict.Evidence, replayedConflict.Evidence);
        AssertRevision(first, oldRead.Revision);
        AssertRevision(second, newRead.Revision);
        Assert.Equal(ContextualRoleRevisionDisposition.Replaced, oldRead.Disposition);
        Assert.Equal(ContextualRoleRevisionDisposition.Active, newRead.Disposition);
        Assert.Equal(ContextualRoleLifecycleState.Active, lifecycle.Snapshot!.State);
        Assert.Equal(second.Identity, lifecycle.Snapshot.CurrentIdentity);
        Assert.Equal("replace-reviewer", lifecycle.Snapshot.LastOperationId);
    }

    [Fact]
    public async Task Disable_reenable_and_tombstone_are_explicit_and_cannot_rewrite_retained_revision()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var revision = Revision("reviewer", 1);
        await store.MutateAsync(CreateRequest("create-reviewer", revision));
        var disabled = await store.MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, revision.Identity));
        var disabledRead = await store.ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));
        var reenabled = await store.MutateAsync(Mutation("reenable-reviewer", ContextualRoleRevisionMutationKind.Reenable, "reviewer", null, revision.Identity));
        var tombstoned = await store.MutateAsync(Mutation("tombstone-reviewer", ContextualRoleRevisionMutationKind.Tombstone, "reviewer", null, revision.Identity));
        var resurrection = await store.MutateAsync(Mutation("reenable-tombstone", ContextualRoleRevisionMutationKind.Reenable, "reviewer", null, revision.Identity));
        var lifecycle = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));
        var historical = await store.ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));

        Assert.Equal(ContextualRoleLifecycleState.Disabled, disabled.Evidence!.State);
        Assert.Equal(ContextualRoleRevisionDisposition.Disabled, disabledRead.Disposition);
        Assert.Equal(ContextualRoleLifecycleState.Active, reenabled.Evidence!.State);
        Assert.Equal(ContextualRoleLifecycleState.Tombstoned, tombstoned.Evidence!.State);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, resurrection.Status);
        Assert.Equal(ContextualRoleLifecycleState.Tombstoned, lifecycle.Snapshot!.State);
        AssertRevision(revision, historical.Revision);
        Assert.Equal(ContextualRoleRevisionDisposition.Tombstoned, historical.Disposition);
    }

    [Fact]
    public async Task Replacing_a_disabled_role_keeps_the_new_revision_disabled_until_explicit_reenable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var first = Revision("reviewer", 1);
        var second = Revision("reviewer", 2, "Replacement");
        await store.MutateAsync(CreateRequest("create-reviewer", first));
        await store.MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, first.Identity));

        var replacement = await store.MutateAsync(Mutation("replace-reviewer", ContextualRoleRevisionMutationKind.Replace, "reviewer", second, first.Identity));
        var replacementRead = await store.ReadAsync(new ContextualRoleRevisionReadRequest(second.Identity));
        var reenabled = await store.MutateAsync(Mutation("reenable-reviewer", ContextualRoleRevisionMutationKind.Reenable, "reviewer", null, second.Identity));

        Assert.Equal(ContextualRoleLifecycleState.Disabled, replacement.Evidence!.State);
        Assert.Equal(ContextualRoleRevisionDisposition.Disabled, replacementRead.Disposition);
        Assert.Equal(ContextualRoleLifecycleState.Active, reenabled.Evidence!.State);
    }

    [Fact]
    public async Task Historical_valid_primary_cannot_roll_back_the_terminal_transition_chain()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var revision = Revision("reviewer", 1);
        await store.MutateAsync(CreateRequest("create-reviewer", revision));
        var statePath = Path.Combine(StoreRoot(workspace.RootPath), "states", "reviewer.json");
        var historicalActive = await File.ReadAllBytesAsync(statePath);
        await store.MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, revision.Identity));
        await File.WriteAllBytesAsync(statePath, historicalActive);

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));
        var mutation = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(Mutation("reenable-reviewer", ContextualRoleRevisionMutationKind.Reenable, "reviewer", null, revision.Identity));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, mutation.Status);
        Assert.Equal(historicalActive, await File.ReadAllBytesAsync(statePath));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("fork")]
    [InlineData("reordered")]
    [InlineData("proof-result-substitution")]
    public async Task Forked_gapped_reordered_or_substituted_transition_evidence_fails_closed(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var revision = Revision("reviewer", 1);
        await store.MutateAsync(CreateRequest("create-reviewer", revision));
        await store.MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, revision.Identity));
        if (scenario is "fork" or "reordered")
        {
            await store.MutateAsync(Mutation("reenable-reviewer", ContextualRoleRevisionMutationKind.Reenable, "reviewer", null, revision.Identity));
        }

        var root = StoreRoot(workspace.RootPath);
        switch (scenario)
        {
            case "gap":
                ResealArtifact(Path.Combine(root, "operations", "disable-reviewer.intent.json"), artifact => SetPlannedSequence(artifact, 3));
                break;
            case "fork":
                var createIntent = ReadArtifact(Path.Combine(root, "operations", "create-reviewer.intent.json"));
                ResealArtifact(Path.Combine(root, "operations", "reenable-reviewer.intent.json"), artifact =>
                {
                    artifact["priorState"] = createIntent["plannedState"]!.DeepClone();
                    SetPlannedSequence(artifact, 2);
                });
                break;
            case "reordered":
                ResealArtifact(Path.Combine(root, "operations", "disable-reviewer.intent.json"), artifact => SetPlannedSequence(artifact, 3));
                ResealArtifact(Path.Combine(root, "operations", "reenable-reviewer.intent.json"), artifact => SetPlannedSequence(artifact, 2));
                break;
            case "proof-result-substitution":
                var createEvidence = ReadArtifact(Path.Combine(root, "proofs", "create-reviewer.json"))["evidence"]!.AsObject();
                ResealArtifact(Path.Combine(root, "proofs", "disable-reviewer.json"), artifact => SubstituteTerminalEvidence(artifact["evidence"]!.AsObject(), createEvidence));
                ResealArtifact(Path.Combine(root, "operations", "disable-reviewer.result.json"), artifact => SubstituteTerminalEvidence(artifact["evidence"]!.AsObject(), createEvidence));
                break;
        }

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
    }

    [Theory]
    [InlineData(ContextualRolePersistenceBoundary.AnchorPublished, ContextualRoleRevisionMutationStatus.Unavailable, ContextualRoleRevisionMutationStatus.Accepted)]
    [InlineData(ContextualRolePersistenceBoundary.IntentPublished, ContextualRoleRevisionMutationStatus.Ambiguous, ContextualRoleRevisionMutationStatus.Recovered)]
    [InlineData(ContextualRolePersistenceBoundary.RevisionPublished, ContextualRoleRevisionMutationStatus.Ambiguous, ContextualRoleRevisionMutationStatus.Recovered)]
    [InlineData(ContextualRolePersistenceBoundary.PrimaryPublished, ContextualRoleRevisionMutationStatus.Ambiguous, ContextualRoleRevisionMutationStatus.Recovered)]
    [InlineData(ContextualRolePersistenceBoundary.ProofPublished, ContextualRoleRevisionMutationStatus.Ambiguous, ContextualRoleRevisionMutationStatus.Accepted)]
    [InlineData(ContextualRolePersistenceBoundary.ResultPublished, ContextualRoleRevisionMutationStatus.Ambiguous, ContextualRoleRevisionMutationStatus.Accepted)]
    public async Task Every_durable_boundary_recovers_to_a_proved_or_explicit_ambiguous_outcome(ContextualRolePersistenceBoundary boundary, ContextualRoleRevisionMutationStatus interruptedStatus, ContextualRoleRevisionMutationStatus recoveredStatus)
    {
        using var workspace = new TestWorkspace();
        var interrupted = false;
        var options = new ContextualRoleRevisionStoreOptions
        {
            DurableBoundaryObserver = (observed, _) =>
            {
                if (!interrupted && observed == boundary)
                {
                    interrupted = true;
                    throw new IOException("Simulated process failure after a durable boundary.");
                }

                return ValueTask.CompletedTask;
            }
        };
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));
        var first = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one", options).MutateAsync(request);
        var retry = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").MutateAsync(request);

        Assert.True(interrupted);
        Assert.Equal(interruptedStatus, first.Status);
        Assert.Equal(recoveredStatus, retry.Status);
        Assert.NotNull(retry.Evidence);
        Assert.Equal(ContextualRoleRevisionReadStatus.Found, (await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(request.Revision!.Identity))).Status);
    }

    [Theory]
    [InlineData(1, ContextualRoleRevisionMutationStatus.Accepted)]
    [InlineData(2, ContextualRoleRevisionMutationStatus.Recovered)]
    [InlineData(3, ContextualRoleRevisionMutationStatus.Recovered)]
    [InlineData(4, ContextualRoleRevisionMutationStatus.Recovered)]
    [InlineData(5, ContextualRoleRevisionMutationStatus.Accepted)]
    [InlineData(6, ContextualRoleRevisionMutationStatus.Accepted)]
    public async Task Every_rename_before_parent_directory_barrier_is_ambiguous_and_exactly_recoverable(int interruptedPublication, ContextualRoleRevisionMutationStatus recoveredStatus)
    {
        using var workspace = new TestWorkspace();
        var publication = 0;
        var options = new ContextualRoleRevisionStoreOptions
        {
            PhysicalBoundaryObserver = (boundary, _) => boundary == ContextualRolePhysicalPersistenceBoundary.AfterTargetFlushBeforeDirectoryFlush && Interlocked.Increment(ref publication) == interruptedPublication
                ? ValueTask.FromException(new IOException("Simulated process loss before the parent-directory metadata barrier."))
                : ValueTask.CompletedTask
        };
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));
        var interrupted = await new ContextualRoleRevisionStore(paths, "workspace-one", options).MutateAsync(request);
        var recovered = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(request);

        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, interrupted.Status);
        Assert.Equal(recoveredStatus, recovered.Status);
        Assert.Equal(ContextualRoleRevisionReadStatus.Found, (await new ContextualRoleRevisionStore(paths, "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(request.Revision!.Identity))).Status);
    }

    [Fact]
    public async Task Failure_before_handle_relative_rename_removes_and_flushes_the_exact_temporary_artifact()
    {
        using var workspace = new TestWorkspace();
        var options = new ContextualRoleRevisionStoreOptions
        {
            PhysicalBoundaryObserver = (boundary, _) => boundary == ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativePublication
                ? ValueTask.FromException(new IOException("Simulated failure before handle-relative rename."))
                : ValueTask.CompletedTask
        };
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));

        var interrupted = await new ContextualRoleRevisionStore(paths, "workspace-one", options).MutateAsync(request);
        var temporaryArtifacts = Directory.EnumerateFiles(StoreRoot(workspace.RootPath), "*.tmp", SearchOption.AllDirectories).ToArray();
        var retried = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(request);

        Assert.Equal(ContextualRoleRevisionMutationStatus.Unavailable, interrupted.Status);
        Assert.Empty(temporaryArtifacts);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, retried.Status);
    }

    [Fact]
    public async Task Temporary_content_substitution_during_publication_is_never_acknowledged()
    {
        using var workspace = new TestWorkspace();
        var publication = 0;
        var substituted = false;
        var options = new ContextualRoleRevisionStoreOptions
        {
            PhysicalBoundaryObserver = (boundary, _) =>
            {
                if (boundary == ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativePublication && Interlocked.Increment(ref publication) == 2)
                {
                    var operationRoot = Path.Combine(StoreRoot(workspace.RootPath), "operations");
                    var temporaryPath = Directory.EnumerateFiles(operationRoot, "*.tmp", SearchOption.TopDirectoryOnly).Single();
                    using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(new byte[checked((int)stream.Length)]);
                    stream.Flush(flushToDisk: true);
                    substituted = true;
                }

                return ValueTask.CompletedTask;
            }
        };
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));

        var result = await new ContextualRoleRevisionStore(paths, "workspace-one", options).MutateAsync(request);
        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.True(substituted);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, result.Status);
        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
    }

    [Fact]
    public async Task Link_count_change_after_target_flush_is_ambiguous_and_recovers_only_after_removal()
    {
        using var workspace = new TestWorkspace();
        var publication = 0;
        var linkPath = Path.Combine(workspace.RootPath, "intent-hardlink.json");
        var options = new ContextualRoleRevisionStoreOptions
        {
            PhysicalBoundaryObserver = (boundary, _) =>
            {
                if (boundary == ContextualRolePhysicalPersistenceBoundary.AfterTargetFlushBeforeDirectoryFlush && Interlocked.Increment(ref publication) == 2)
                {
                    CreateHardLink(linkPath, Path.Combine(StoreRoot(workspace.RootPath), "operations", "create-reviewer.intent.json"));
                }

                return ValueTask.CompletedTask;
            }
        };
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));

        var interrupted = await new ContextualRoleRevisionStore(paths, "workspace-one", options).MutateAsync(request);
        File.Delete(linkPath);
        var recovered = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(request);

        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, interrupted.Status);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Recovered, recovered.Status);
    }

    [Fact]
    public async Task Cancellation_after_durable_intent_is_ambiguous_and_exact_retry_recovers()
    {
        using var workspace = new TestWorkspace();
        var options = new ContextualRoleRevisionStoreOptions
        {
            DurableBoundaryObserver = (boundary, token) => boundary == ContextualRolePersistenceBoundary.IntentPublished
                ? ValueTask.FromException(new OperationCanceledException(token))
                : ValueTask.CompletedTask
        };
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));
        var interrupted = await new ContextualRoleRevisionStore(paths, "workspace-one", options).MutateAsync(request);
        var recovered = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(request);

        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, interrupted.Status);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Recovered, recovered.Status);
        Assert.True(recovered.Evidence!.Recovered);
    }

    [Fact]
    public async Task Interrupted_stale_precondition_recovers_the_same_conflict_without_mutating_primary_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", 1);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", revision));
        var options = new ContextualRoleRevisionStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) => boundary == ContextualRolePersistenceBoundary.IntentPublished
                ? ValueTask.FromException(new IOException("Simulated conflict crash."))
                : ValueTask.CompletedTask
        };
        var stale = Mutation("stale-disable", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, new ContextualRoleRevisionIdentity("reviewer", 2));
        var interrupted = await new ContextualRoleRevisionStore(paths, "workspace-one", options).MutateAsync(stale);
        var recovered = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(stale);
        var lifecycle = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, interrupted.Status);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, recovered.Status);
        Assert.True(recovered.Evidence!.Recovered);
        Assert.Equal(ContextualRoleLifecycleState.Active, lifecycle.Snapshot!.State);
        Assert.Equal("create-reviewer", lifecycle.Snapshot.LastOperationId);
    }

    [Fact]
    public async Task Copied_workspace_artifacts_fail_closed_even_when_the_logical_workspace_id_is_reused()
    {
        using var source = new TestWorkspace();
        using var target = new TestWorkspace();
        var revision = Revision("reviewer", 1);
        await new ContextualRoleRevisionStore(new WorkspacePaths(source.RootPath), "workspace-one").MutateAsync(CreateRequest("create-reviewer", revision));
        CopyDirectory(Path.Combine(source.RootPath, ".agent", "contextual-roles"), Path.Combine(target.RootPath, ".agent", "contextual-roles"));

        var copied = await new ContextualRoleRevisionStore(new WorkspacePaths(target.RootPath), "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));
        var changedIdentity = await new ContextualRoleRevisionStore(new WorkspacePaths(source.RootPath), "workspace-two").ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));

        Assert.Equal(ContextualRoleRevisionReadStatus.Ambiguous, copied.Status);
        Assert.Equal(ContextualRoleRevisionReadStatus.Ambiguous, changedIdentity.Status);
    }

    [Fact]
    public async Task Unknown_or_malformed_proof_blocks_new_mutation_without_changing_proved_primary_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var revision = Revision("reviewer", 1);
        await store.MutateAsync(CreateRequest("create-reviewer", revision));
        var statePath = Path.Combine(workspace.RootPath, ".agent", "contextual-roles", "states", "reviewer.json");
        var proofPath = Path.Combine(workspace.RootPath, ".agent", "contextual-roles", "proofs", "create-reviewer.json");
        var originalState = await File.ReadAllBytesAsync(statePath);
        var proof = await File.ReadAllTextAsync(proofPath);
        await File.WriteAllTextAsync(proofPath, proof.Insert(1, "\"unknownField\":true,"));

        var result = await store.MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, revision.Identity));

        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, result.Status);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(statePath));
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, ".agent", "contextual-roles", "operations", "disable-reviewer.intent.json")));
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("revision")]
    [InlineData("state")]
    [InlineData("intent")]
    [InlineData("proof")]
    [InlineData("result")]
    public async Task Integrity_corruption_in_every_artifact_family_fails_closed(string artifactFamily)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", 1);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", revision));
        CorruptIntegrityHash(ArtifactPath(workspace.RootPath, artifactFamily));

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
    }

    [Theory]
    [InlineData("revisions/reviewer.1.json", "revisions/writer.1.json")]
    [InlineData("states/reviewer.json", "states/writer.json")]
    [InlineData("operations/create-reviewer.intent.json", "operations/other.intent.json")]
    [InlineData("proofs/create-reviewer.json", "proofs/other.json")]
    [InlineData("operations/create-reviewer.result.json", "operations/other.result.json")]
    public async Task Renamed_artifacts_cannot_substitute_for_their_content_identity(string originalRelativePath, string changedRelativePath)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", Revision("reviewer", 1)));
        var root = StoreRoot(workspace.RootPath);
        File.Move(Path.Combine(root, originalRelativePath), Path.Combine(root, changedRelativePath));

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
    }

    [Theory]
    [InlineData("missing-anchor")]
    [InlineData("missing-proof")]
    [InlineData("orphan-result")]
    [InlineData("unattributed-state")]
    [InlineData("unknown-operation")]
    [InlineData("unknown-revision-file")]
    [InlineData("oversized-anchor")]
    public async Task Missing_unknown_or_oversized_artifacts_fail_closed(string scenario)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", Revision("reviewer", 1)));
        var root = StoreRoot(workspace.RootPath);
        switch (scenario)
        {
            case "missing-anchor":
                File.Delete(Path.Combine(root, "workspace-anchor.json"));
                break;
            case "missing-proof":
                File.Delete(Path.Combine(root, "proofs", "create-reviewer.json"));
                break;
            case "orphan-result":
                File.Delete(Path.Combine(root, "operations", "create-reviewer.intent.json"));
                break;
            case "unattributed-state":
                File.Delete(Path.Combine(root, "operations", "create-reviewer.intent.json"));
                File.Delete(Path.Combine(root, "operations", "create-reviewer.result.json"));
                File.Delete(Path.Combine(root, "proofs", "create-reviewer.json"));
                break;
            case "unknown-operation":
                File.WriteAllText(Path.Combine(root, "operations", "unknown.json"), "{}");
                break;
            case "unknown-revision-file":
                File.WriteAllText(Path.Combine(root, "revisions", "unknown.txt"), "{}");
                break;
            case "oversized-anchor":
                File.WriteAllBytes(Path.Combine(root, "workspace-anchor.json"), new byte[65 * 1024]);
                break;
        }

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
    }

    [Fact]
    public async Task Unknown_store_files_and_nested_artifact_directories_are_not_ignored()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", 1);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", revision));
        var root = Path.Combine(workspace.RootPath, ".agent", "contextual-roles");
        await File.WriteAllTextAsync(Path.Combine(root, "unknown.json"), "{}");
        var unknownFile = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));
        File.Delete(Path.Combine(root, "unknown.json"));
        Directory.CreateDirectory(Path.Combine(root, "revisions", "nested"));
        var nested = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));

        Assert.Equal(ContextualRoleRevisionReadStatus.Ambiguous, unknownFile.Status);
        Assert.Equal(ContextualRoleRevisionReadStatus.Ambiguous, nested.Status);
    }

    [Fact]
    public async Task Existing_agent_without_store_is_not_mistaken_for_contextual_role_persistence()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, ".agent"));

        var read = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleLifecycleReadStatus.NotFound, read.Status);
    }

    [Fact]
    public async Task Unknown_top_level_directory_and_stale_temporary_artifact_are_rejected_or_cleaned_deterministically()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", 1);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", revision));
        var root = StoreRoot(workspace.RootPath);
        var unknownDirectory = Path.Combine(root, "unknown");
        Directory.CreateDirectory(unknownDirectory);

        var rejected = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));
        Directory.Delete(unknownDirectory);
        var temporaryPath = Path.Combine(root, "operations", ".orphan.tmp");
        await File.WriteAllTextAsync(temporaryPath, "orphan");
        var accepted = await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, revision.Identity));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, rejected.Status);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, accepted.Status);
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task Symbolic_link_substitution_fails_closed_without_following_the_artifact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var external = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", 1);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", revision));
        var statePath = Path.Combine(workspace.RootPath, ".agent", "contextual-roles", "states", "reviewer.json");
        var externalState = Path.Combine(external.RootPath, "state.json");
        File.Copy(statePath, externalState);
        File.Delete(statePath);
        File.CreateSymbolicLink(statePath, externalState);

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
        Assert.True(File.Exists(externalState));
    }

    [Fact]
    public async Task Transient_root_swap_during_validation_cannot_hide_an_unknown_retained_entry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", Revision("reviewer", 1)));
        var root = StoreRoot(workspace.RootPath);
        var substituteRoot = root + "-substitute";
        var retainedRoot = root + "-retained";
        CopyDirectory(root, substituteRoot);
        File.WriteAllText(Path.Combine(root, "unknown-entry.json"), "{}");
        var swapAttempted = false;
        var swapBlocked = false;
        var restored = false;
        var options = new ContextualRoleRevisionStoreOptions
        {
            PhysicalBoundaryObserver = (boundary, _) =>
            {
                if (boundary == ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativeValidationEnumeration && !swapAttempted)
                {
                    swapAttempted = true;
                    try
                    {
                        Directory.Move(root, retainedRoot);
                        try
                        {
                            Directory.Move(substituteRoot, root);
                        }
                        catch
                        {
                            Directory.Move(retainedRoot, root);
                            throw;
                        }
                    }
                    catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
                    {
                        swapBlocked = true;
                    }
                }
                else if (boundary == ContextualRolePhysicalPersistenceBoundary.AfterHandleRelativeValidationEnumeration && Directory.Exists(retainedRoot))
                {
                    Directory.Move(root, substituteRoot);
                    Directory.Move(retainedRoot, root);
                    restored = true;
                }

                return ValueTask.CompletedTask;
            }
        };

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one", options).ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.True(swapAttempted);
        Assert.True(swapBlocked || restored);
        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
        Assert.True(File.Exists(Path.Combine(root, "unknown-entry.json")));
    }

    [Fact]
    public async Task Transient_subdirectory_swaps_during_validation_cannot_make_a_historical_primary_terminal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = Revision("reviewer", 1);
        var second = Revision("reviewer", 2, "Replacement");
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", first));
        var root = StoreRoot(workspace.RootPath);
        var historicalState = File.ReadAllBytes(Path.Combine(root, "states", "reviewer.json"));
        var substitutedDirectories = new Dictionary<int, string>
        {
            [2] = "revisions",
            [4] = "operations",
            [5] = "proofs"
        };
        foreach (var name in substitutedDirectories.Values)
        {
            CopyDirectory(Path.Combine(root, name), Path.Combine(workspace.RootPath, $"historical-{name}"));
        }

        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(Mutation("replace-reviewer", ContextualRoleRevisionMutationKind.Replace, "reviewer", second, first.Identity));
        File.WriteAllBytes(Path.Combine(root, "states", "reviewer.json"), historicalState);
        var enumeration = 0;
        var swappedDirectories = 0;
        var restoredDirectories = 0;
        var swapBlocked = false;
        string? activeDirectory = null;
        var options = new ContextualRoleRevisionStoreOptions
        {
            PhysicalBoundaryObserver = (boundary, _) =>
            {
                if (boundary == ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativeValidationEnumeration)
                {
                    var currentEnumeration = Interlocked.Increment(ref enumeration);
                    if (substitutedDirectories.TryGetValue(currentEnumeration, out var name))
                    {
                        var canonical = Path.Combine(root, name);
                        var retained = Path.Combine(workspace.RootPath, $"retained-{name}");
                        var substitute = Path.Combine(workspace.RootPath, $"historical-{name}");
                        try
                        {
                            Directory.Move(canonical, retained);
                            try
                            {
                                Directory.Move(substitute, canonical);
                                activeDirectory = name;
                                swappedDirectories++;
                            }
                            catch
                            {
                                Directory.Move(retained, canonical);
                                throw;
                            }
                        }
                        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
                        {
                            swapBlocked = true;
                        }
                    }
                }
                else if (boundary == ContextualRolePhysicalPersistenceBoundary.AfterHandleRelativeValidationEnumeration && activeDirectory is { } name)
                {
                    var canonical = Path.Combine(root, name);
                    var retained = Path.Combine(workspace.RootPath, $"retained-{name}");
                    var substitute = Path.Combine(workspace.RootPath, $"historical-{name}");
                    Directory.Move(canonical, substitute);
                    Directory.Move(retained, canonical);
                    activeDirectory = null;
                    restoredDirectories++;
                }

                return ValueTask.CompletedTask;
            }
        };

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one", options).ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.True(swapBlocked || swappedDirectories == substitutedDirectories.Count);
        Assert.Equal(swappedDirectories, restoredDirectories);
        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
        Assert.Equal(historicalState, File.ReadAllBytes(Path.Combine(root, "states", "reviewer.json")));
        Assert.True(File.Exists(Path.Combine(root, "operations", "replace-reviewer.intent.json")));
    }

    [Fact]
    public async Task Linked_artifact_directory_is_rejected_without_following_it_on_every_supported_host()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", 1);
        using (var store = new ContextualRoleRevisionStore(paths, "workspace-one"))
        {
            await store.MutateAsync(CreateRequest("create-reviewer", revision));
        }

        var revisionsPath = Path.Combine(StoreRoot(workspace.RootPath), "revisions");
        var externalRevisionsPath = Path.Combine(workspace.RootPath, "external-revisions");
        Directory.Move(revisionsPath, externalRevisionsPath);
        CreateDirectoryLink(revisionsPath, externalRevisionsPath);

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));

        Assert.Equal(ContextualRoleRevisionReadStatus.Ambiguous, read.Status);
        Assert.True(File.Exists(Path.Combine(externalRevisionsPath, "reviewer.1.json")));
    }

    [Fact]
    public async Task Dangling_store_link_is_not_treated_as_an_absent_store()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var root = StoreRoot(workspace.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.CreateSymbolicLink(root, Path.Combine(workspace.RootPath, "missing-target"));

        var read = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));

        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, read.Status);
    }

    [Fact]
    public async Task Hardlinked_artifact_is_rejected_by_retained_handle_link_count()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", 1);
        await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(CreateRequest("create-reviewer", revision));
        var revisionPath = Path.Combine(StoreRoot(workspace.RootPath), "revisions", "reviewer.1.json");
        CreateHardLink(Path.Combine(workspace.RootPath, "revision-hardlink.json"), revisionPath);

        var read = await new ContextualRoleRevisionStore(paths, "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(revision.Identity));

        Assert.Equal(ContextualRoleRevisionReadStatus.Ambiguous, read.Status);
    }

    [Fact]
    public async Task Root_swap_during_handle_relative_publication_never_writes_into_the_substituted_store()
    {
        using var workspace = new TestWorkspace();
        var root = StoreRoot(workspace.RootPath);
        var retainedRoot = root + "-retained";
        var publication = 0;
        var swapped = false;
        var swapBlocked = false;
        var options = new ContextualRoleRevisionStoreOptions
        {
            PhysicalBoundaryObserver = (boundary, _) =>
            {
                if (!swapped && boundary == ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativePublication && Interlocked.Increment(ref publication) == 2)
                {
                    try
                    {
                        Directory.Move(root, retainedRoot);
                        Directory.CreateDirectory(root);
                        foreach (var child in new[] { "revisions", "states", "operations", "proofs" })
                        {
                            Directory.CreateDirectory(Path.Combine(root, child));
                        }

                        swapped = true;
                    }
                    catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
                    {
                        swapBlocked = true;
                    }
                }

                return ValueTask.CompletedTask;
            }
        };
        var request = CreateRequest("create-reviewer", Revision("reviewer", 1));

        var result = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one", options).MutateAsync(request);

        if (swapBlocked)
        {
            Assert.False(swapped);
            Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, result.Status);
            Assert.True(File.Exists(Path.Combine(root, "operations", "create-reviewer.intent.json")));
            return;
        }

        Assert.True(swapped);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Ambiguous, result.Status);
        Assert.False(File.Exists(Path.Combine(root, "operations", "create-reviewer.intent.json")));
        Assert.True(File.Exists(Path.Combine(retainedRoot, "operations", "create-reviewer.intent.json")));
    }

    [Fact]
    public async Task Quota_exhaustion_returns_unavailable_before_publishing_an_intent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new ContextualRoleRevisionStoreOptions { MaxOperationArtifacts = 1 };
        var store = new ContextualRoleRevisionStore(paths, "workspace-one", options);
        var revision = Revision("reviewer", 1);
        await store.MutateAsync(CreateRequest("create-reviewer", revision));
        var result = await store.MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, revision.Identity));

        Assert.Equal(ContextualRoleRevisionMutationStatus.Unavailable, result.Status);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, ".agent", "contextual-roles", "operations", "disable-reviewer.intent.json")));

        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await new ContextualRoleRevisionStore(paths, "workspace-one").MutateAsync(Mutation("disable-reviewer", ContextualRoleRevisionMutationKind.Disable, "reviewer", null, revision.Identity))).Status);
        var boundedReader = new ContextualRoleRevisionStore(paths, "workspace-one", new ContextualRoleRevisionStoreOptions { MaxOperationArtifacts = 1 });
        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, (await boundedReader.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"))).Status);
    }

    [Fact]
    public async Task Conflict_with_requested_revision_fits_the_exact_existing_revision_and_byte_boundaries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var timeProvider = new FixedTimeProvider(_requestedAt.AddHours(1));
        await new ContextualRoleRevisionStore(paths, "workspace-one", timeProvider: timeProvider).MutateAsync(CreateRequest("create-reviewer", Revision("reviewer", 1)));
        var conflict = Mutation("stale-replace", ContextualRoleRevisionMutationKind.Replace, "reviewer", Revision("reviewer", 3, "Replacement"), new ContextualRoleRevisionIdentity("reviewer", 2));
        var measured = await new ContextualRoleRevisionStore(paths, "workspace-one", timeProvider: timeProvider).MutateAsync(conflict);
        var root = StoreRoot(workspace.RootPath);
        var exactByteCeiling = ArtifactBytes(root);
        File.Delete(Path.Combine(root, "operations", "stale-replace.intent.json"));
        File.Delete(Path.Combine(root, "operations", "stale-replace.result.json"));
        File.Delete(Path.Combine(root, "proofs", "stale-replace.json"));
        var belowOptions = new ContextualRoleRevisionStoreOptions { MaxRevisionArtifacts = 1, MaxTotalArtifactBytes = exactByteCeiling - 1 };
        var options = new ContextualRoleRevisionStoreOptions { MaxRevisionArtifacts = 1, MaxTotalArtifactBytes = exactByteCeiling };

        var belowBoundary = await new ContextualRoleRevisionStore(paths, "workspace-one", belowOptions, timeProvider).MutateAsync(conflict);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Unavailable, belowBoundary.Status);
        Assert.False(File.Exists(Path.Combine(root, "operations", "stale-replace.intent.json")));
        Assert.False(File.Exists(Path.Combine(root, "operations", "stale-replace.result.json")));
        Assert.False(File.Exists(Path.Combine(root, "proofs", "stale-replace.json")));
        var bounded = await new ContextualRoleRevisionStore(paths, "workspace-one", options, timeProvider).MutateAsync(conflict);

        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, measured.Status);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, bounded.Status);
        Assert.Equal(exactByteCeiling, ArtifactBytes(root));
        Assert.True(File.Exists(Path.Combine(root, "operations", "stale-replace.intent.json")));
        Assert.True(File.Exists(Path.Combine(root, "operations", "stale-replace.result.json")));
        Assert.True(File.Exists(Path.Combine(root, "proofs", "stale-replace.json")));
        Assert.False(File.Exists(Path.Combine(root, "revisions", "reviewer.3.json")));
    }

    [Fact]
    public async Task Cross_process_mutation_owner_makes_other_operations_unavailable_until_release()
    {
        using var workspace = new TestWorkspace();
        using var process = StartMutationHost(workspace.RootPath);
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var other = CreateRequest("create-writer", Revision("writer", 1));
            var blocked = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").MutateAsync(other);
            var blockedRead = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));
            var blockedRevisionRead = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").ReadAsync(new ContextualRoleRevisionReadRequest(new ContextualRoleRevisionIdentity("reviewer", 1)));
            Assert.Equal(ContextualRoleRevisionMutationStatus.Unavailable, blocked.Status);
            Assert.Equal(ContextualRoleLifecycleReadStatus.Unavailable, blockedRead.Status);
            Assert.Equal(ContextualRoleRevisionReadStatus.Unavailable, blockedRevisionRead.Status);

            await process.StandardInput.WriteLineAsync("release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            var accepted = await new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one").MutateAsync(other);
            Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, accepted.Status);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static ContextualRoleRevisionMutationRequest CreateRequest(string operationId, ContextualRoleRevision revision)
        => Mutation(operationId, ContextualRoleRevisionMutationKind.Create, revision.Identity.RoleId, revision, null);

    private static ContextualRoleRevisionMutationRequest Mutation(string operationId, ContextualRoleRevisionMutationKind kind, string roleId, ContextualRoleRevision? revision, ContextualRoleRevisionIdentity? expected)
        => ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(operationId, string.Empty, kind, roleId, "user-jake", revision, expected, _requestedAt));

    private static ContextualRoleRevision Revision(string roleId, int revision, string displayName = "Reviewer")
    {
        var value = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity(roleId, revision),
            string.Empty,
            displayName,
            "Provide bounded review assistance.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("user-jake", _requestedAt, _requestedAt),
            new ContextualRoleWorkspaceApplicability(ImmutableArray.Create("workspace-one")),
            new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, $"{roleId}-source", ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(ImmutableArray<string>.Empty));
        return ContextualRoleRevisionContentHash.Apply(value);
    }

    private static void AssertRevision(ContextualRoleRevision expected, ContextualRoleRevision? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Purpose, actual.Purpose);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Provenance, actual.Provenance);
        Assert.Equal(expected.WorkspaceApplicability.WorkspaceIds.ToArray(), actual.WorkspaceApplicability.WorkspaceIds.ToArray());
        Assert.Equal(expected.InstructionSource, actual.InstructionSource);
        Assert.Equal(expected.PolicyMaxima.CapabilityIds.ToArray(), actual.PolicyMaxima.CapabilityIds.ToArray());
    }

    private static string StoreRoot(string workspaceRoot) => Path.Combine(workspaceRoot, ".agent", "contextual-roles");

    private static long ArtifactBytes(string root) => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

    private static string ArtifactPath(string workspaceRoot, string artifactFamily)
    {
        var root = StoreRoot(workspaceRoot);
        return artifactFamily switch
        {
            "anchor" => Path.Combine(root, "workspace-anchor.json"),
            "revision" => Path.Combine(root, "revisions", "reviewer.1.json"),
            "state" => Path.Combine(root, "states", "reviewer.json"),
            "intent" => Path.Combine(root, "operations", "create-reviewer.intent.json"),
            "proof" => Path.Combine(root, "proofs", "create-reviewer.json"),
            "result" => Path.Combine(root, "operations", "create-reviewer.result.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(artifactFamily))
        };
    }

    private static void CorruptIntegrityHash(string path)
    {
        var json = File.ReadAllText(path);
        const string Marker = "\"integrityHash\":\"";
        var hashStart = json.LastIndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        Assert.True(hashStart >= Marker.Length);
        var replacement = json[hashStart] == '0' ? '1' : '0';
        File.WriteAllText(path, json[..hashStart] + replacement + json[(hashStart + 1)..]);
    }

    private static JsonObject ReadArtifact(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void ResealArtifact(string path, Action<JsonObject> mutate)
    {
        var artifact = ReadArtifact(path);
        mutate(artifact);
        SealArtifact(artifact);
        File.WriteAllText(path, artifact.ToJsonString());
    }

    private static void SetPlannedSequence(JsonObject artifact, long sequence)
    {
        var planned = artifact["plannedState"]!.AsObject();
        planned["sequence"] = sequence;
        SealArtifact(planned);
    }

    private static void SubstituteTerminalEvidence(JsonObject target, JsonObject source)
    {
        target["previousIdentity"] = null;
        target["previousStateHash"] = null;
        target["currentIdentity"] = source["currentIdentity"]!.DeepClone();
        target["currentStateHash"] = source["currentStateHash"]!.DeepClone();
        target["sequence"] = source["sequence"]!.DeepClone();
        target["state"] = source["state"]!.DeepClone();
    }

    private static void SealArtifact(JsonObject artifact)
    {
        artifact["integrityHash"] = string.Empty;
        artifact["integrityHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifact.ToJsonString()))).ToLowerInvariant();
    }

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(WindowsCreateHardLink(linkPath, existingPath, IntPtr.Zero));
            return;
        }

        Assert.Equal(0, UnixCreateHardLink(existingPath, linkPath));
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath }
        }) ?? throw new InvalidOperationException("The Windows directory-junction helper did not start.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsCreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int UnixCreateHardLink(string existingPath, string newPath);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal));
        }
    }

    private static Process StartMutationHost(string workspaceRoot)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Contextual-role mutation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("hold-contextual-role");
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The contextual-role mutation host process could not be started.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root could not be located from the test output directory.");
    }
}
