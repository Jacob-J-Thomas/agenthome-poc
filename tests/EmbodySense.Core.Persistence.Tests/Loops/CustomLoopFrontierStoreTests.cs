using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Tests.Loops.Admission;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopFrontierStoreTests
{
    private const string CrashProbeCandidatePathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_FRONTIER_CRASH_CANDIDATE";
    private const string CrashProbeLockPathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_FRONTIER_CRASH_LOCK";
    private const string CrashProbeReadyPathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_FRONTIER_CRASH_READY";
    private const string CrashProbeReleasePathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_FRONTIER_CRASH_RELEASE";
    private const string CrashProbeStagingPathVariable = "EMBODYSENSE_TEST_CUSTOM_LOOP_FRONTIER_CRASH_STAGING";

    [Fact]
    public void Canonical_codec_round_trips_exact_frontier_and_explicit_legacy_null()
    {
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(context.Run.Frontier);

        var hydrated = CustomLoopRunArtifactSerializer.Deserialize(CustomLoopRunArtifactSerializer.Serialize(context.Run));

        var hydratedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(hydrated.Frontier);
        Assert.NotSame(frontier, hydratedFrontier);
        Assert.NotSame(frontier.Binding, hydratedFrontier.Binding);
        Assert.NotSame(frontier.Payload, hydratedFrontier.Payload);
        Assert.NotSame(frontier.Payload.Nodes, hydratedFrontier.Payload.Nodes);
        Assert.Equal(
            CustomLoopRunArtifactSerializer.Serialize(context.Run),
            CustomLoopRunArtifactSerializer.Serialize(hydrated));
        Assert.Equal(frontier.Payload.ContentHash, hydratedFrontier.Payload.ContentHash);
        Assert.True(GovernedLoopFrontierContractHash.Matches(hydratedFrontier));
        Assert.All(hydratedFrontier.Payload.Nodes, node =>
        {
            Assert.Equal(node.PlanOrdinal, node.ActivationOrdinal);
            Assert.Equal(1, node.VisitOrdinal);
            Assert.Null(node.CycleId);
            Assert.Null(node.CycleIteration);
            Assert.Null(node.ControlOutcome);
            Assert.Empty(node.SelectedControlEdgeIds);
            Assert.Empty(node.SkippedControlEdgeIds);
            Assert.Empty(node.JoinArrivals);
        });

        var first = frontier.Payload.Nodes[0];
        var routedCycleActivation = GovernedLoopNodeExecutionEvidence.CreateActivation(
            first.ActivationOrdinal,
            first.PlanOrdinal,
            first.VisitOrdinal,
            first.NodeId,
            first.Descriptor,
            first.IncomingControlEdgeIds,
            first.OutgoingControlEdgeIds,
            first.Status,
            first.Attempt,
            first.AttemptOperationId,
            first.OutcomeEvidenceId,
            first.OutcomeEvidenceHash,
            "cycle-main",
            1,
            GovernedLoopControlCondition.Always,
            first.OutgoingControlEdgeIds,
            [],
            []);
        var enrichedFrontier = GovernedLoopFrontierPosture.Create(
            frontier.Binding,
            frontier.WorkspaceId,
            frontier.GraphArtifactHash,
            frontier.GraphLayoutHash,
            frontier.AdmissionReceiptHash,
            frontier.Payload.FrontierVersion,
            frontier.Payload.ConcurrencyCeiling,
            frontier.Payload.Status,
            [routedCycleActivation, .. frontier.Payload.Nodes.Skip(1)],
            frontier.Payload.UpdatedAtUtc,
            string.Empty);
        var enriched = context.Run with { Frontier = enrichedFrontier };
        var hydratedEnriched = CustomLoopRunArtifactSerializer.Deserialize(CustomLoopRunArtifactSerializer.Serialize(enriched));
        var enrichedActivation = Assert.IsType<GovernedLoopFrontierPosture>(hydratedEnriched.Frontier).Payload.Nodes[0];
        Assert.Equal("cycle-main", enrichedActivation.CycleId);
        Assert.Equal(1, enrichedActivation.CycleIteration);
        Assert.Equal(GovernedLoopControlCondition.Always, enrichedActivation.ControlOutcome);
        Assert.Equal(first.OutgoingControlEdgeIds, enrichedActivation.SelectedControlEdgeIds);

        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(
            context.Run.AdmittedDefinition.CapabilityRequirements,
            context.Run.UpdatedAtUtc);
        var legacy = CustomLoopAdmissionRequestHash.Apply(context.Run with
        {
            CapabilityAdmission = capabilityAdmission,
            SequentialInvocationSnapshot = null,
            SequentialAdapterBinding = null,
            Frontier = null,
            Events = [context.Run.Events[0] with { SequentialNodeEvidence = null }],
        });
        var hydratedLegacy = CustomLoopRunArtifactSerializer.Deserialize(CustomLoopRunArtifactSerializer.Serialize(legacy));
        Assert.Null(hydratedLegacy.Frontier);
    }

    [Fact]
    public void Codec_rejects_missing_reordered_unknown_unsupported_numeric_bounded_hash_substituted_and_null_frontier_shapes()
    {
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        var encoded = CustomLoopRunArtifactSerializer.Serialize(context.Run);
        var root = JsonNode.Parse(encoded)!.AsObject();
        Assert.True(root["run"]!.AsObject().Remove("frontier"));
        Assert.Throws<FormatException>(() => Deserialize(root));

        root = JsonNode.Parse(encoded)!.AsObject();
        var frontier = root["run"]!["frontier"]!.AsObject();
        root["run"]!["frontier"] = ReverseProperties(frontier);
        Assert.Throws<FormatException>(() => Deserialize(root));

        root = JsonNode.Parse(encoded)!.AsObject();
        root["run"]!["frontier"] = null;
        Assert.Throws<FormatException>(() => Deserialize(root));

        foreach (var corrupt in new Action<JsonObject>[]
        {
            candidate => candidate["run"]!["frontier"]!.AsObject()["unknown"] = true,
            candidate => candidate["run"]!["frontier"]!["schemaVersion"] = 2,
            candidate => candidate["run"]!["frontier"]!["payload"]!["schemaVersion"] = 2,
            candidate => candidate["run"]!["frontier"]!["payload"]!["nodes"]![0]!["schemaVersion"] = 2,
            candidate => candidate["run"]!["frontier"]!["payload"]!["status"] = 1,
            candidate => candidate["run"]!["frontier"]!["payload"]!["nodes"]![0]!["status"] = 3,
            candidate => candidate["run"]!["frontier"]!["payload"]!["status"] = "future",
            candidate => candidate["run"]!["frontier"]!["payload"]!["concurrencyCeiling"] = 2,
            candidate => candidate["run"]!["frontier"]!["payload"]!["frontierVersion"] = 0,
            RemoveActivationShape,
            candidate => candidate["run"]!["frontier"]!["payload"]!["nodes"]![0]!["activationOrdinal"] = 1,
            candidate => candidate["run"]!["frontier"]!["payload"]!["nodes"]![0]!["visitOrdinal"] = 2,
            candidate => candidate["run"]!["frontier"]!["payload"]!["nodes"]![0]!["cycleId"] = "cycle-main",
            candidate => candidate["run"]!["frontier"]!["payload"]!["nodes"]![0]!["controlOutcome"] = "future",
            AddSelectedEdgeWithoutOutcome,
            AddMalformedJoinArrival,
            AddTooManyJoinArrivals,
            candidate => candidate["run"]!["frontier"]!["payload"]!["nodes"]![1]!["planOrdinal"] = 2,
            AddTooManyNodes,
            AddTooManyOutgoingEdges,
        })
        {
            root = JsonNode.Parse(encoded)!.AsObject();
            corrupt(root);
            Assert.Throws<FormatException>(() => Deserialize(root));
        }

        var contentHash = context.Run.Frontier!.Payload.ContentHash;
        var text = Encoding.UTF8.GetString(encoded);
        var substituted = text.Replace(contentHash, new string('9', 64), StringComparison.Ordinal);
        Assert.NotEqual(text, substituted);
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(substituted)));
    }

    [Fact]
    public async Task Store_create_and_restart_retain_the_exact_hash_bound_frontier()
    {
        using var workspace = new TestWorkspace();
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        var paths = new WorkspacePaths(workspace.RootPath);
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var loaded = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(context.Run.Id));
        Assert.Equal(
            CustomLoopRunArtifactSerializer.Serialize(context.Run),
            CustomLoopRunArtifactSerializer.Serialize(loaded));
        Assert.True(GovernedLoopFrontierContractHash.Matches(loaded.Frontier));
    }

    [Fact]
    public async Task Store_restarts_retain_running_advanced_and_completed_frontiers()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        }

        var active = await UpdateAndRestartAsync(paths, TransitionToRunning(context.Run, "event-running"), 1);
        Assert.Equal(CustomLoopRunStatus.Running, active.Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, active.Frontier!.Payload.Nodes[1].Status);

        var running = await UpdateAndRestartAsync(paths, StartInference(active, context, "event-inference-start"), 2);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, running.Frontier!.Payload.Nodes[1].Status);

        var completion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            "event-inference-completed",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            context.Binding,
            context.Plan.Nodes[1],
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            running.UpdatedAtUtc.AddMinutes(1));
        var advanced = await UpdateAndRestartAsync(paths, CompleteInference(running, context, completion), 3);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, advanced.Frontier!.Payload.Nodes[1].Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, advanced.Frontier.Payload.Nodes[2].Status);

        var exitRunning = await UpdateAndRestartAsync(paths, StartExit(advanced, context, "event-exit-start"), 4);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, exitRunning.Frontier!.Payload.Nodes[2].Status);
        var completed = await UpdateAndRestartAsync(paths, CompleteExit(exitRunning, context, "event-exit-completed"), 5);
        Assert.Equal(CustomLoopRunStatus.Completed, completed.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Completed, completed.Frontier!.Payload.Status);
        Assert.All(completed.Frontier.Payload.Nodes, node => Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, node.Status));
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, completed.Events[^1].Kind);
        Assert.Equal(CustomLoopRunEventKind.CheckpointCommitted, completed.Events[^2].Kind);
        Assert.Equal(completed.Events[^2].Sequence, completed.Checkpoint.LastCommittedSequence);
        Assert.True(GovernedLoopFrontierContractHash.Matches(completed.Frontier));
    }

    [Theory]
    [InlineData(CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.NeedsReview, GovernedLoopNodeExecutionStatus.ReviewBlocked, GovernedLoopFrontierStatus.ReviewBlocked, CustomLoopRunStatus.NeedsReview)]
    [InlineData(CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected, GovernedLoopNodeExecutionStatus.Failed, GovernedLoopFrontierStatus.Failed, CustomLoopRunStatus.Failed)]
    public async Task Store_restarts_retain_review_blocked_and_failed_frontiers(
        CustomLoopSequentialNodeEvidenceKind evidenceKind,
        CustomLoopSequentialNodeDisposition disposition,
        GovernedLoopNodeExecutionStatus nodeStatus,
        GovernedLoopFrontierStatus frontierStatus,
        CustomLoopRunStatus runStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        }

        var active = await UpdateAndRestartAsync(paths, TransitionToRunning(context.Run, "event-running"), 1);
        var running = await UpdateAndRestartAsync(paths, StartInference(active, context, "event-inference-start"), 2);
        var terminal = CompleteWithFailurePosture(
            running,
            context,
            evidenceKind,
            disposition,
            nodeStatus,
            frontierStatus,
            runStatus);
        var loaded = await UpdateAndRestartAsync(paths, terminal, 3);

        Assert.Equal(runStatus, loaded.Status);
        Assert.Equal(frontierStatus, loaded.Frontier!.Payload.Status);
        Assert.Equal(nodeStatus, loaded.Frontier.Payload.Nodes[1].Status);
        Assert.Equal(loaded.Events[^2].EventId, loaded.Frontier.Payload.Nodes[1].OutcomeEvidenceId);
        Assert.Equal(loaded.Events[^2].SequentialNodeEvidence!.OutcomeArtifactHash, loaded.Frontier.Payload.Nodes[1].OutcomeEvidenceHash);
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, loaded.Events[^1].Kind);
        Assert.True(GovernedLoopFrontierContractHash.Matches(loaded.Frontier));
    }

    [Fact]
    public async Task Concurrent_frontier_event_checkpoint_and_lifecycle_successors_have_one_cross_store_cas_winner()
    {
        using var workspace = new TestWorkspace();
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var firstStore = new CustomLoopRunStore(paths);
        using var secondStore = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await firstStore.CreateAsync(context.Run)).Status);
        var active = TransitionToRunning(context.Run, "event-running");
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await firstStore.UpdateAsync(active, 1)).Status);
        var running = StartInference(active, context, "event-start");
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await firstStore.UpdateAsync(running, 2)).Status);
        var firstCompletion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            "event-completed-a",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            context.Binding,
            context.Plan.Nodes[1],
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            running.UpdatedAtUtc.AddMinutes(1));
        var secondCompletion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            "event-completed-b",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            context.Binding,
            context.Plan.Nodes[1],
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            running.UpdatedAtUtc.AddMinutes(1));
        var first = CommitCheckpoint(CompleteInference(running, context, firstCompletion), "checkpoint-a");
        var second = CommitCheckpoint(CompleteInference(running, context, secondCompletion), "checkpoint-b");

        var results = await Task.WhenAll(
            firstStore.UpdateAsync(first, running.LifecycleVersion),
            secondStore.UpdateAsync(second, running.LifecycleVersion));

        Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Updated);
        Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Conflict);
        var winner = Assert.IsType<CustomLoopRunRecord>(await firstStore.GetAsync(context.Run.Id));
        Assert.Equal(4, winner.LifecycleVersion);
        Assert.Equal(3, winner.Frontier!.Payload.FrontierVersion);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, winner.Frontier.Payload.Nodes[1].Status);
        Assert.Equal(winner.Events[^2].EventId, winner.Frontier.Payload.Nodes[1].OutcomeEvidenceId);
        Assert.Equal(CustomLoopRunEventKind.CheckpointCommitted, winner.Events[^1].Kind);
        Assert.Equal(winner.Events[^1].Sequence, winner.Checkpoint.LastCommittedSequence);
        Assert.Equal(1, winner.Checkpoint.NextStepIndex);
        Assert.NotEqual(
            winner.Events.Any(item => string.Equals(item.EventId, firstCompletion.EventId, StringComparison.Ordinal)),
            winner.Events.Any(item => string.Equals(item.EventId, secondCompletion.EventId, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Outcome_frontier_requires_retained_event_and_commits_atomically_with_it()
    {
        using var workspace = new TestWorkspace();
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var running = StartInference(context.Run, context, "event-inference-start");
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, 1)).Status);
        var completion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            "event-inference-completed",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            context.Binding,
            context.Plan.Nodes[1],
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            running.UpdatedAtUtc.AddMinutes(1));
        var atomic = CompleteInference(running, context, completion);
        var missingOutcome = atomic with { Events = running.Events };

        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(missingOutcome, running.LifecycleVersion));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(atomic, running.LifecycleVersion)).Status);

        using var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var loaded = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(context.Run.Id));
        Assert.Equal(completion.EventId, loaded.Frontier!.Payload.Nodes[1].OutcomeEvidenceId);
        Assert.Equal(completion.SequentialNodeEvidence!.OutcomeArtifactHash, loaded.Frontier.Payload.Nodes[1].OutcomeEvidenceHash);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, loaded.Frontier.Payload.Nodes[1].Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, loaded.Frontier.Payload.Nodes[2].Status);
        Assert.True(GovernedLoopFrontierContractHash.Matches(loaded.Frontier));
    }

    [Fact]
    public async Task Corrupt_frontier_is_fail_closed_without_rewrite_and_orphaned_staging_does_not_replace_the_durable_frontier()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        }

        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, context.Run.LoopId, context.Run.Id + ".json");
        var stagingPath = Path.Combine(
            Path.GetDirectoryName(artifactPath)!,
            $".{context.Run.Id}.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(stagingPath, "partially flushed replacement without a complete frontier");

        using (var restarted = new CustomLoopRunStore(paths))
        {
            var loaded = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(context.Run.Id));
            Assert.Equal(context.Run.Frontier!.Payload.ContentHash, loaded.Frontier!.Payload.ContentHash);
            Assert.Equal(1, (await restarted.GetTraceQuotaAsync()).RetainedTraceCount);
        }
        Assert.False(File.Exists(stagingPath));

        var original = await File.ReadAllBytesAsync(artifactPath);
        var corrupt = JsonNode.Parse(original)!.AsObject();
        corrupt["run"]!["frontier"]!["payload"]!["status"] = "future";
        var corruptBytes = Encoding.UTF8.GetBytes(corrupt.ToJsonString() + "\n");
        await File.WriteAllBytesAsync(artifactPath, corruptBytes);

        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(context.Run.Id));
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(artifactPath));
    }

    [Fact]
    public async Task External_process_crash_before_rename_preserves_the_prior_atomic_frontier_and_cleans_only_the_orphan()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        }

        var active = await UpdateAndRestartAsync(paths, TransitionToRunning(context.Run, "event-running"), 1);
        var running = await UpdateAndRestartAsync(paths, StartInference(active, context, "event-inference-start"), 2);
        var inferenceCompletion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            "event-inference-completed",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            context.Binding,
            context.Plan.Nodes[1],
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            running.UpdatedAtUtc.AddMinutes(1));
        var advanced = await UpdateAndRestartAsync(paths, CompleteInference(running, context, inferenceCompletion), 3);
        var prior = await UpdateAndRestartAsync(paths, StartExit(advanced, context, "event-exit-start"), 4);
        var candidate = CompleteExit(prior, context, "event-exit-completed-after-crash");
        var validation = CustomLoopRunValidator.ValidateUpdate(prior, candidate);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        var candidateBytes = CustomLoopRunArtifactSerializer.Serialize(candidate);
        var priorBytes = CustomLoopRunArtifactSerializer.Serialize(prior);
        var candidatePath = workspace.File("frontier-crash-candidate.json");
        var readyPath = workspace.File("frontier-crash-ready");
        var releasePath = workspace.File("frontier-crash-release");
        var runDirectory = Path.Combine(paths.CustomLoopRunsPath, prior.LoopId);
        var canonicalPath = Path.Combine(runDirectory, prior.Id + ".json");
        var stagingPath = Path.Combine(runDirectory, $".{prior.Id}.json.{Guid.NewGuid():N}.tmp");
        var lockPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-runs.lock");
        await File.WriteAllBytesAsync(candidatePath, candidateBytes);
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(canonicalPath));

        using var child = StartFrontierCrashProbe(lockPath, stagingPath, candidatePath, readyPath, releasePath);
        var outputTask = child.StandardOutput.ReadToEndAsync();
        var errorTask = child.StandardError.ReadToEndAsync();
        try
        {
            await WaitForCrashProbeReadyAsync(
                readyPath,
                candidate.Frontier!.Payload.ContentHash,
                child,
                outputTask,
                errorTask,
                TimeSpan.FromSeconds(60));
            Assert.False(child.HasExited);
            Assert.Equal(candidate.Frontier!.Payload.ContentHash, await File.ReadAllTextAsync(readyPath));
            Assert.Equal(candidateBytes, await File.ReadAllBytesAsync(stagingPath));
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(canonicalPath));

            using (var contendingStore = new CustomLoopRunStore(paths))
            using (var contention = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => contendingStore.GetTraceQuotaAsync(contention.Token));
            }
            Assert.True(File.Exists(stagingPath));
            using (var lockFreeReader = new CustomLoopRunStore(paths))
            {
                Assert.Equal(priorBytes, CustomLoopRunArtifactSerializer.Serialize(
                    Assert.IsType<CustomLoopRunRecord>(await lockFreeReader.GetAsync(prior.Id))));
            }

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEqual(0, child.ExitCode);
            Assert.True(File.Exists(stagingPath));

            using var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
            var beforeRecovery = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(prior.Id));
            Assert.Equal(priorBytes, CustomLoopRunArtifactSerializer.Serialize(beforeRecovery));
            Assert.Equal(1, (await restarted.GetTraceQuotaAsync()).RetainedTraceCount);
            Assert.False(File.Exists(stagingPath));
            Assert.Equal(candidateBytes, await File.ReadAllBytesAsync(candidatePath));
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(canonicalPath));

            var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(prior.Id));
            Assert.Equal(prior.LifecycleVersion, recovered.LifecycleVersion);
            Assert.Equal(prior.Checkpoint.LastCommittedSequence, recovered.Checkpoint.LastCommittedSequence);
            Assert.Equal(prior.Frontier!.Payload.ContentHash, recovered.Frontier!.Payload.ContentHash);
            Assert.Equal(prior.Events.Select(item => item.EventId), recovered.Events.Select(item => item.EventId));
            Assert.DoesNotContain(recovered.Events, item =>
                string.Equals(item.EventId, "event-exit-completed-after-crash", StringComparison.Ordinal));
            Assert.Equal(GovernedLoopNodeExecutionStatus.Running, recovered.Frontier.Payload.Nodes[2].Status);
            Assert.Equal(CustomLoopRunStatus.Running, recovered.Status);
        }
        finally
        {
            await File.WriteAllTextAsync(releasePath, "release");
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            _ = await outputTask;
            _ = await errorTask;
        }
    }

    [Fact]
    public void Crash_probe_ready_marker_accepts_only_complete_expected_evidence()
    {
        const string Expected = "0123456789abcdef";

        Assert.False(IsCompleteCrashProbeReadyMarker(string.Empty, Expected));
        Assert.False(IsCompleteCrashProbeReadyMarker("01234567", Expected));
        Assert.True(IsCompleteCrashProbeReadyMarker(Expected, Expected));
        Assert.Throws<InvalidDataException>(() => IsCompleteCrashProbeReadyMarker("0123456x", Expected));
        Assert.Throws<InvalidDataException>(() => IsCompleteCrashProbeReadyMarker(Expected + "0", Expected));
    }

    [Fact]
    public async Task Crash_probe_ready_marker_is_published_from_a_closed_file_without_overwriting_existing_evidence()
    {
        using var workspace = new TestWorkspace();
        var readyPath = workspace.File("frontier-crash-ready");
        const string Expected = "0123456789abcdef";

        await PublishCrashProbeReadyMarkerAsync(readyPath, Expected);

        Assert.Equal(Expected, await File.ReadAllTextAsync(readyPath));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "frontier-crash-ready.*.tmp"));
        await Assert.ThrowsAsync<IOException>(() => PublishCrashProbeReadyMarkerAsync(readyPath, "replacement"));
        Assert.Equal(Expected, await File.ReadAllTextAsync(readyPath));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "frontier-crash-ready.*.tmp"));
    }

    [Fact]
    public async Task External_process_crash_probe_child_stages_one_authenticated_successor_while_holding_the_mutation_lease()
    {
        var candidatePath = Environment.GetEnvironmentVariable(CrashProbeCandidatePathVariable);
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return;
        }

        var lockPath = RequireCrashProbePath(CrashProbeLockPathVariable);
        var stagingPath = RequireCrashProbePath(CrashProbeStagingPathVariable);
        var readyPath = RequireCrashProbePath(CrashProbeReadyPathVariable);
        var releasePath = RequireCrashProbePath(CrashProbeReleasePathVariable);
        var candidateBytes = await File.ReadAllBytesAsync(candidatePath);
        var candidate = CustomLoopRunArtifactSerializer.Deserialize(candidateBytes);
        Assert.Equal(candidateBytes, CustomLoopRunArtifactSerializer.Serialize(candidate));
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(candidate.Frontier);
        Assert.Equal(CustomLoopRunStatus.Completed, candidate.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Completed, frontier.Payload.Status);
        Assert.Equal(CustomLoopRunEventKind.CheckpointCommitted, candidate.Events[^2].Kind);
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, candidate.Events[^1].Kind);
        Assert.Equal(candidate.Events[^2].Sequence, candidate.Checkpoint.LastCommittedSequence);

        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        await using var lease = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        await using (var staging = new FileStream(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await staging.WriteAsync(candidateBytes);
            await staging.FlushAsync();
            staging.Flush(flushToDisk: true);
        }

        await PublishCrashProbeReadyMarkerAsync(readyPath, frontier.Payload.ContentHash);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(releasePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(15), timeout.Token);
        }
    }

    private static CustomLoopRunRecord StartInference(
        CustomLoopRunRecord run,
        CustomLoopSequentialEvidenceStoreTests.SequentialContext context,
        string eventId)
    {
        var node = context.Plan.Nodes[1];
        var updatedAtUtc = run.UpdatedAtUtc.AddMinutes(1);
        var start = CreateSequentialEvent(
            run.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.NodeAttemptStarted,
            context.Binding,
            node,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            updatedAtUtc);
        var runningNode = GovernedLoopNodeExecutionEvidence.Create(
            node.Ordinal,
            node.NodeId,
            node.Descriptor,
            ControlEdges(node.IncomingControlEdgeId),
            ControlEdges(node.OutgoingControlEdgeId),
            GovernedLoopNodeExecutionStatus.Running,
            1,
            start.EventId);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            UpdatedAtUtc = updatedAtUtc,
            Events = [.. run.Events, start],
            Frontier = ReplaceFrontier(run.Frontier!, [run.Frontier!.Payload.Nodes[0], runningNode], GovernedLoopFrontierStatus.Active, updatedAtUtc),
        };
    }

    private static CustomLoopRunRecord TransitionToRunning(CustomLoopRunRecord run, string eventId)
    {
        var updatedAtUtc = run.UpdatedAtUtc.AddMinutes(1);
        var lifecycle = CreateRunEvent(
            run.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.LifecycleChanged,
            updatedAtUtc,
            expectedLifecycleVersion: run.LifecycleVersion);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = updatedAtUtc,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = updatedAtUtc },
            Events = [.. run.Events, lifecycle],
        };
    }

    private static CustomLoopRunRecord CommitCheckpoint(CustomLoopRunRecord run, string eventId)
    {
        var updatedAtUtc = run.UpdatedAtUtc.AddSeconds(1);
        var checkpoint = CreateRunEvent(
            run.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.CheckpointCommitted,
            updatedAtUtc,
            iteration: run.Checkpoint.Iteration);
        return run with
        {
            UpdatedAtUtc = updatedAtUtc,
            Checkpoint = run.Checkpoint with
            {
                NextStepIndex = 1,
                LastCommittedSequence = checkpoint.Sequence,
            },
            Events = [.. run.Events, checkpoint],
        };
    }

    private static CustomLoopRunRecord CompleteInference(
        CustomLoopRunRecord running,
        CustomLoopSequentialEvidenceStoreTests.SequentialContext context,
        CustomLoopRunEvent completion)
    {
        var node = context.Plan.Nodes[1];
        var exit = context.Plan.Nodes[2];
        var completedNode = GovernedLoopNodeExecutionEvidence.Create(
            node.Ordinal,
            node.NodeId,
            node.Descriptor,
            ControlEdges(node.IncomingControlEdgeId),
            ControlEdges(node.OutgoingControlEdgeId),
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            running.Frontier!.Payload.Nodes[1].AttemptOperationId,
            completion.EventId,
            completion.SequentialNodeEvidence!.OutcomeArtifactHash);
        var readyExit = GovernedLoopNodeExecutionEvidence.Create(
            exit.Ordinal,
            exit.NodeId,
            exit.Descriptor,
            ControlEdges(exit.IncomingControlEdgeId),
            ControlEdges(exit.OutgoingControlEdgeId),
            GovernedLoopNodeExecutionStatus.Ready);
        return running with
        {
            LifecycleVersion = running.LifecycleVersion + 1,
            UpdatedAtUtc = completion.TimestampUtc,
            Events = [.. running.Events, completion],
            Frontier = ReplaceFrontier(
                running.Frontier,
                [running.Frontier.Payload.Nodes[0], completedNode, readyExit],
                GovernedLoopFrontierStatus.Active,
                completion.TimestampUtc),
        };
    }

    private static CustomLoopRunRecord StartExit(
        CustomLoopRunRecord run,
        CustomLoopSequentialEvidenceStoreTests.SequentialContext context,
        string eventId)
    {
        var exit = context.Plan.Nodes[2];
        var updatedAtUtc = run.UpdatedAtUtc.AddMinutes(1);
        var start = CreateSequentialEvent(
            run.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.ExitDecisionStarted,
            context.Binding,
            exit,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            updatedAtUtc);
        var runningExit = GovernedLoopNodeExecutionEvidence.Create(
            exit.Ordinal,
            exit.NodeId,
            exit.Descriptor,
            ControlEdges(exit.IncomingControlEdgeId),
            ControlEdges(exit.OutgoingControlEdgeId),
            GovernedLoopNodeExecutionStatus.Running,
            1,
            start.EventId);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            UpdatedAtUtc = updatedAtUtc,
            Events = [.. run.Events, start],
            Frontier = ReplaceFrontier(
                run.Frontier!,
                [run.Frontier!.Payload.Nodes[0], run.Frontier.Payload.Nodes[1], runningExit],
                GovernedLoopFrontierStatus.Active,
                updatedAtUtc),
        };
    }

    private static CustomLoopRunRecord CompleteExit(
        CustomLoopRunRecord running,
        CustomLoopSequentialEvidenceStoreTests.SequentialContext context,
        string eventId)
    {
        var exit = context.Plan.Nodes[2];
        var outcomeAtUtc = running.UpdatedAtUtc.AddMinutes(1);
        var completion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.ExitDecisionCompleted,
            context.Binding,
            exit,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            outcomeAtUtc);
        var completedExit = GovernedLoopNodeExecutionEvidence.Create(
            exit.Ordinal,
            exit.NodeId,
            exit.Descriptor,
            ControlEdges(exit.IncomingControlEdgeId),
            ControlEdges(exit.OutgoingControlEdgeId),
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            running.Frontier!.Payload.Nodes[2].AttemptOperationId,
            completion.EventId,
            completion.SequentialNodeEvidence!.OutcomeArtifactHash);
        var checkpointAtUtc = outcomeAtUtc.AddSeconds(1);
        var checkpoint = CreateRunEvent(
            completion.Sequence + 1,
            "checkpoint-terminal",
            CustomLoopRunEventKind.CheckpointCommitted,
            checkpointAtUtc,
            iteration: running.Checkpoint.Iteration);
        var terminalAtUtc = checkpointAtUtc.AddSeconds(1);
        var lifecycle = CreateRunEvent(
            checkpoint.Sequence + 1,
            "event-completed",
            CustomLoopRunEventKind.LifecycleChanged,
            terminalAtUtc,
            expectedLifecycleVersion: running.LifecycleVersion);
        return running with
        {
            LifecycleVersion = running.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Completed,
            UpdatedAtUtc = terminalAtUtc,
            CompletedAtUtc = terminalAtUtc,
            ExecutionClock = StopClock(running.ExecutionClock, terminalAtUtc),
            Checkpoint = running.Checkpoint with
            {
                NextStepIndex = 1,
                LastCommittedSequence = checkpoint.Sequence,
            },
            Events = [.. running.Events, completion, checkpoint, lifecycle],
            FinalOutput = "done",
            Frontier = ReplaceFrontier(
                running.Frontier,
                [running.Frontier.Payload.Nodes[0], running.Frontier.Payload.Nodes[1], completedExit],
                GovernedLoopFrontierStatus.Completed,
                outcomeAtUtc),
        };
    }

    private static CustomLoopRunRecord CompleteWithFailurePosture(
        CustomLoopRunRecord running,
        CustomLoopSequentialEvidenceStoreTests.SequentialContext context,
        CustomLoopSequentialNodeEvidenceKind evidenceKind,
        CustomLoopSequentialNodeDisposition disposition,
        GovernedLoopNodeExecutionStatus nodeStatus,
        GovernedLoopFrontierStatus frontierStatus,
        CustomLoopRunStatus runStatus)
    {
        var node = context.Plan.Nodes[1];
        var outcomeAtUtc = running.UpdatedAtUtc.AddMinutes(1);
        var outcome = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            runStatus == CustomLoopRunStatus.NeedsReview ? "event-review-required" : "event-failed",
            CustomLoopRunEventKind.NodeAttemptFailed,
            context.Binding,
            node,
            evidenceKind,
            disposition,
            outcomeAtUtc);
        var outcomeNode = GovernedLoopNodeExecutionEvidence.Create(
            node.Ordinal,
            node.NodeId,
            node.Descriptor,
            ControlEdges(node.IncomingControlEdgeId),
            ControlEdges(node.OutgoingControlEdgeId),
            nodeStatus,
            1,
            running.Frontier!.Payload.Nodes[1].AttemptOperationId,
            outcome.EventId,
            outcome.SequentialNodeEvidence!.OutcomeArtifactHash);
        var terminalAtUtc = outcomeAtUtc.AddSeconds(1);
        var lifecycle = CreateRunEvent(
            outcome.Sequence + 1,
            runStatus == CustomLoopRunStatus.NeedsReview ? "event-needs-review" : "event-run-failed",
            CustomLoopRunEventKind.LifecycleChanged,
            terminalAtUtc,
            expectedLifecycleVersion: running.LifecycleVersion);
        return running with
        {
            LifecycleVersion = running.LifecycleVersion + 1,
            Status = runStatus,
            UpdatedAtUtc = terminalAtUtc,
            CompletedAtUtc = terminalAtUtc,
            ExecutionClock = StopClock(running.ExecutionClock, terminalAtUtc),
            Events = [.. running.Events, outcome, lifecycle],
            FailureCode = runStatus == CustomLoopRunStatus.NeedsReview ? "frontier_needs_review" : "frontier_failed",
            FailureDetail = runStatus == CustomLoopRunStatus.NeedsReview
                ? "The exact retained outcome requires review."
                : "The exact retained outcome conclusively failed.",
            Frontier = ReplaceFrontier(
                running.Frontier,
                [running.Frontier.Payload.Nodes[0], outcomeNode],
                frontierStatus,
                outcomeAtUtc),
        };
    }

    private static CustomLoopExecutionClock StopClock(CustomLoopExecutionClock clock, DateTimeOffset stoppedAtUtc)
    {
        var accumulated = clock.AccumulatedRunningMilliseconds;
        if (clock.ActiveSinceUtc is { } activeSinceUtc)
        {
            accumulated += (long)(stoppedAtUtc - activeSinceUtc).TotalMilliseconds;
        }

        return new CustomLoopExecutionClock(accumulated, null);
    }

    private static async Task<CustomLoopRunRecord> UpdateAndRestartAsync(
        WorkspacePaths paths,
        CustomLoopRunRecord candidate,
        int expectedLifecycleVersion)
    {
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(
                CustomLoopRunStoreStatus.Updated,
                (await store.UpdateAsync(candidate, expectedLifecycleVersion)).Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        return Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(candidate.Id));
    }

    private static GovernedLoopFrontierPosture ReplaceFrontier(
        GovernedLoopFrontierPosture current,
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes,
        GovernedLoopFrontierStatus status,
        DateTimeOffset updatedAtUtc)
    {
        var payload = GovernedLoopFrontierPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.FrontierVersion + 1,
            current.Payload.ConcurrencyCeiling,
            status,
            nodes,
            updatedAtUtc,
            string.Empty);
        return GovernedLoopFrontierPosture.Create(
            current.Binding,
            current.WorkspaceId,
            current.GraphArtifactHash,
            current.GraphLayoutHash,
            current.AdmissionReceiptHash,
            payload);
    }

    private static CustomLoopRunEvent CreateSequentialEvent(
        long sequence,
        string eventId,
        CustomLoopRunEventKind kind,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopSequentialPlanNode node,
        CustomLoopSequentialNodeEvidenceKind evidenceKind,
        CustomLoopSequentialNodeDisposition disposition,
        DateTimeOffset timestampUtc)
    {
        var runEvent = new CustomLoopRunEvent(
            sequence,
            eventId,
            timestampUtc,
            kind,
            1,
            node.NodeId,
            1,
            kind.ToString(),
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            kind == CustomLoopRunEventKind.ExitDecisionCompleted ? CustomLoopExitDecision.Complete : null,
            TraceReservationUtf8Bytes: kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted
                ? CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes
                : null);
        var controlOutcome = evidenceKind switch
        {
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted => (GovernedLoopControlCondition?)null,
            _ when node.Descriptor.Kind == GovernedLoopNodeKind.Trigger => GovernedLoopControlCondition.Always,
            _ when disposition == CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopControlCondition.Failure,
            _ => GovernedLoopControlCondition.Success,
        };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            1,
            evidenceKind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            node.Ordinal,
            1,
            node.NodeId,
            1,
            node.CycleId,
            node.CycleId is null ? null : 1,
            controlOutcome,
            controlOutcome is null || controlOutcome == GovernedLoopControlCondition.Failure ? [] : node.OutgoingControlEdgeIds.ToArray(),
            controlOutcome == GovernedLoopControlCondition.Failure ? node.OutgoingControlEdgeIds.ToArray() : [],
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent CreateRunEvent(
        long sequence,
        string eventId,
        CustomLoopRunEventKind kind,
        DateTimeOffset timestampUtc,
        int? iteration = null,
        int? expectedLifecycleVersion = null)
    {
        return new CustomLoopRunEvent(
            sequence,
            eventId,
            timestampUtc,
            kind,
            iteration,
            null,
            null,
            kind.ToString(),
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ControlExpectedLifecycleVersion: expectedLifecycleVersion);
    }

    private static Process StartFrontierCrashProbe(
        string lockPath,
        string stagingPath,
        string candidatePath,
        string readyPath,
        string releasePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Verification.CoverageChildProcessAssembly.AddVstestArguments(
            startInfo,
            typeof(CustomLoopFrontierStoreTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopFrontierStoreTests.External_process_crash_probe_child_stages_one_authenticated_successor_while_holding_the_mutation_lease");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrashProbeLockPathVariable] = lockPath;
        startInfo.Environment[CrashProbeStagingPathVariable] = stagingPath;
        startInfo.Environment[CrashProbeCandidatePathVariable] = candidatePath;
        startInfo.Environment[CrashProbeReadyPathVariable] = readyPath;
        startInfo.Environment[CrashProbeReleasePathVariable] = releasePath;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The frontier crash-probe child process did not start.");
    }

    private static async Task WaitForCrashProbeReadyAsync(
        string readyPath,
        string expectedMarker,
        Process process,
        Task<string> outputTask,
        Task<string> errorTask,
        TimeSpan timeout)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(readyPath)
                && IsCompleteCrashProbeReadyMarker(await File.ReadAllTextAsync(readyPath), expectedMarker))
            {
                return;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The frontier crash-probe child exited before staging its successor with code {process.ExitCode}."
                    + $"{Environment.NewLine}{await outputTask}{Environment.NewLine}{await errorTask}");
            }

            if (wait.Elapsed >= timeout)
            {
                throw new TimeoutException("The frontier crash-probe child did not stage its successor within the bounded wait.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(15));
        }
    }

    private static bool IsCompleteCrashProbeReadyMarker(string marker, string expectedMarker)
    {
        if (string.Equals(marker, expectedMarker, StringComparison.Ordinal))
        {
            return true;
        }

        if (marker.Length < expectedMarker.Length && expectedMarker.StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        throw new InvalidDataException("The frontier crash-probe ready marker contains malformed evidence.");
    }

    private static async Task PublishCrashProbeReadyMarkerAsync(string readyPath, string marker)
    {
        var stagingPath = $"{readyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(stagingPath, marker);
            File.Move(stagingPath, readyPath);
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static string RequireCrashProbePath(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"The frontier crash-probe path `{variable}` is required.");
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException($"The frontier crash-probe path `{variable}` must be fully qualified.");
        }

        return value;
    }

    private static CustomLoopRunRecord Deserialize(JsonObject root)
        => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n"));

    private static JsonObject ReverseProperties(JsonObject value)
    {
        var reordered = new JsonObject();
        foreach (var property in value.Reverse())
        {
            reordered[property.Key] = property.Value?.DeepClone();
        }

        return reordered;
    }

    private static void AddTooManyNodes(JsonObject root)
    {
        var nodes = root["run"]!["frontier"]!["payload"]!["nodes"]!.AsArray();
        var template = nodes[1]!.AsObject();
        for (var ordinal = nodes.Count; ordinal <= GovernedLoopExecutionLimits.MaxFrontierNodes; ordinal++)
        {
            var clone = template.DeepClone().AsObject();
            clone["planOrdinal"] = ordinal;
            clone["nodeId"] = $"node-{ordinal}";
            nodes.Add(clone);
        }
    }

    private static void RemoveActivationShape(JsonObject root)
    {
        var node = root["run"]!["frontier"]!["payload"]!["nodes"]![0]!.AsObject();
        Assert.True(node.Remove("activationOrdinal"));
        Assert.True(node.Remove("visitOrdinal"));
        Assert.True(node.Remove("cycleId"));
        Assert.True(node.Remove("cycleIteration"));
        Assert.True(node.Remove("controlOutcome"));
        Assert.True(node.Remove("selectedControlEdgeIds"));
        Assert.True(node.Remove("skippedControlEdgeIds"));
        Assert.True(node.Remove("joinArrivals"));
    }

    private static void AddSelectedEdgeWithoutOutcome(JsonObject root)
    {
        var node = root["run"]!["frontier"]!["payload"]!["nodes"]![0]!;
        var outgoing = node["outgoingControlEdgeIds"]!.AsArray();
        node["selectedControlEdgeIds"]!.AsArray().Add(outgoing[0]!.GetValue<string>());
    }

    private static void AddMalformedJoinArrival(JsonObject root)
    {
        root["run"]!["frontier"]!["payload"]!["nodes"]![1]!["joinArrivals"]!.AsArray().Add(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["controlEdgeId"] = "edge-trigger-inference-1",
            ["sourceActivationOrdinal"] = 1,
        });
    }

    private static void AddTooManyJoinArrivals(JsonObject root)
    {
        var arrivals = root["run"]!["frontier"]!["payload"]!["nodes"]![1]!["joinArrivals"]!.AsArray();
        for (var index = 0; index <= GovernedLoopExecutionLimits.MaxJoinArrivals; index++)
        {
            arrivals.Add(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["controlEdgeId"] = $"edge-{index:D3}",
                ["sourceActivationOrdinal"] = 0,
            });
        }
    }

    private static void AddTooManyOutgoingEdges(JsonObject root)
    {
        var outgoing = root["run"]!["frontier"]!["payload"]!["nodes"]![0]!["outgoingControlEdgeIds"]!.AsArray();
        outgoing.Clear();
        for (var index = 0; index <= GovernedLoopExecutionLimits.MaxOutgoingEdges; index++)
        {
            outgoing.Add($"edge-{index}");
        }
    }

    private static string[] ControlEdges(string? edgeId)
        => edgeId is null ? [] : [edgeId];
}
