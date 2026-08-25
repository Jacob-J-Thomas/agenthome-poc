using System.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Tests.Loops.Admission;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
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
    public async Task Human_review_admission_commits_a_real_running_predecessor_to_its_exact_blocked_frontier_and_round_trips_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext();
        var admittedDefinition = CustomLoopDefinition.CreateSeed("legacy-custom-loop", "default-role", "step-1", "create-loop", context.Run.CreatedAtUtc);
        var admitted = CustomLoopAdmissionRequestHash.Apply(context.Run with { LoopId = admittedDefinition.Id, AdmittedDefinition = admittedDefinition });
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var active = TransitionToRunning(admitted, "review-admission-running");
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(active, admitted.LifecycleVersion)).Status);
        var running = StartInference(active, context, "review-admission-start");
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, active.LifecycleVersion)).Status);
        var commitAtUtc = running.UpdatedAtUtc.AddMinutes(1);
        var transition = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(running.Frontier, context.Binding, null, null, commitAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, transition.Status);
        var blocked = Assert.IsType<GovernedLoopFrontierPosture>(transition.Frontier);
        var request = CreateHumanReviewRequest(running, blocked, running.UpdatedAtUtc);
        var service = new HumanReviewAdmissionService(store);
        var quotaBeforeAdmission = await store.GetTraceQuotaAsync();

        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(running.Id, running.LifecycleVersion, request, blocked));

        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        var quotaAfterAdmission = await store.GetTraceQuotaAsync();
        Assert.Equal(1, quotaAfterAdmission.RetainedTraceCount);
        Assert.True(quotaAfterAdmission.ActualTraceUtf8Bytes > quotaBeforeAdmission.ActualTraceUtf8Bytes);
        Assert.True(quotaAfterAdmission.AccountedTraceUtf8Bytes <= quotaAfterAdmission.MaximumWorkspaceUtf8Bytes);
        Assert.False(quotaAfterAdmission.IsOverLimit);
        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(running.Id));
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Equal(CustomLoopRunStatus.Paused, persisted.Status);
        Assert.Equal(blocked.Payload.ContentHash, persisted.Frontier!.Payload.ContentHash);
        Assert.Equal(blocked.Binding.Revision.GraphId, persisted.HumanReview!.Request.Binding.GraphId);
        Assert.NotEqual(persisted.AdmittedDefinition.Id, persisted.HumanReview.Request.Binding.GraphId);
        Assert.Equal(request.RequestHash, persisted.HumanReview.Request.RequestHash);
        Assert.Equal(120_000, persisted.ExecutionClock.AccumulatedRunningMilliseconds);
        Assert.Null(persisted.ExecutionClock.ActiveSinceUtc);
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, persisted.Events[^2].Kind);
        Assert.Equal(CustomLoopRunEventKind.HumanReviewRequestAdmitted, persisted.Events[^1].Kind);
        Assert.Equal(persisted.HumanReview.Evidence.Single().EvidenceHash, persisted.Events[^1].HumanReviewEvidence?.EvidenceHash);
        Assert.NotSame(request, persisted.HumanReview.Request);
        Assert.NotSame(request.Binding, persisted.HumanReview.Request.Binding);
        Assert.NotSame(blocked, persisted.Frontier);
        Assert.NotSame(blocked.Payload, persisted.Frontier.Payload);
        Assert.True(CustomLoopRunValidator.HasExactDurableEventPrefix(running, persisted));
        var replay = await new HumanReviewAdmissionService(restarted).AdmitAsync(new HumanReviewAdmissionCommand(persisted.Id, running.LifecycleVersion, request, blocked));
        Assert.Equal(CustomLoopRunStoreStatus.AlreadyCreated, replay.Status);
    }

    [Fact]
    public async Task Durable_version_and_prefix_authentication_reject_independently_valid_human_review_state_substitution()
    {
        using var workspace = new TestWorkspace();
        var original = await PersistHumanReviewAdmissionAsync(new WorkspacePaths(workspace.RootPath), "durable-comparison");
        var originalState = Assert.IsType<HumanReviewRunState>(original.HumanReview);
        var substitutedProvenance = HumanReviewContractHash.ApplyProvenance(originalState.Lifecycle.Provenance with { SourceId = "human-review-substitute", ProvenanceHash = string.Empty });
        var substitutedLifecycle = HumanReviewContractHash.ApplyLifecycle(originalState.Lifecycle with { Provenance = substitutedProvenance, LifecycleHash = string.Empty });
        var substituted = original with { HumanReview = originalState with { Lifecycle = substitutedLifecycle, LifecycleHistory = [substitutedLifecycle] } };
        var laterSubstituted = substituted with { LifecycleVersion = substituted.LifecycleVersion + 1, UpdatedAtUtc = substituted.UpdatedAtUtc.AddTicks(1) };

        Assert.True(CustomLoopRunValidator.Validate(substituted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(substituted).Errors));
        Assert.True(CustomLoopRunValidator.Validate(laterSubstituted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(laterSubstituted).Errors));
        Assert.False(CustomLoopRunValidator.HasSameDurableVersion(original, substituted));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(original, substituted));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(original, laterSubstituted));
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task External_process_loss_during_real_human_review_admission_leaves_only_the_intact_predecessor_or_complete_atomic_successor(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var predecessor = await PersistRealRunningPredecessorAsync(paths);
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(predecessor);
        using var process = CancellationHostProcess.Start("human-review-admission-process-loss", workspace.RootPath, predecessor.Id, boundary.ToString());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("test host process crashed", error, StringComparison.OrdinalIgnoreCase);
        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(predecessor.Id));
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        if (persisted.HumanReview is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(persisted));
            return;
        }

        Assert.Equal(CustomLoopRunStatus.Paused, persisted.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, persisted.Frontier!.Payload.Status);
        Assert.Equal("review-request-process-loss", persisted.HumanReview.Request.RequestId);
        Assert.Equal(2, persisted.Events.Length - predecessor.Events.Length);
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, persisted.Events[^2].Kind);
        Assert.Equal(CustomLoopRunEventKind.HumanReviewRequestAdmitted, persisted.Events[^1].Kind);
        Assert.Equal(persisted.HumanReview.Evidence.Single().EvidenceHash, persisted.Events[^1].HumanReviewEvidence?.EvidenceHash);
    }

    [Fact]
    public async Task Two_external_processes_with_distinct_valid_human_review_requests_reach_cas_and_persist_exactly_one_winner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var predecessor = await PersistRealRunningPredecessorAsync(paths);
        var releasePath = workspace.File("human-review-race-release");
        var firstReadyPath = workspace.File("human-review-race-first-ready");
        var secondReadyPath = workspace.File("human-review-race-second-ready");
        var firstResultPath = workspace.File("human-review-race-first-result");
        var secondResultPath = workspace.File("human-review-race-second-result");
        using var first = CancellationHostProcess.Start("human-review-admission-race", workspace.RootPath, predecessor.Id, "race-first", firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.Start("human-review-admission-race", workspace.RootPath, predecessor.Id, "race-second", secondReadyPath, releasePath, secondResultPath);
        var firstOutput = first.StandardOutput.ReadToEndAsync();
        var firstError = first.StandardError.ReadToEndAsync();
        var secondOutput = second.StandardOutput.ReadToEndAsync();
        var secondError = second.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFileAsync(firstReadyPath, TimeSpan.FromSeconds(30));
            await WaitForFileAsync(secondReadyPath, TimeSpan.FromSeconds(30));
            await File.WriteAllTextAsync(releasePath, "release");
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!first.HasExited)
            {
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync();
            }

            if (!second.HasExited)
            {
                second.Kill(entireProcessTree: true);
                await second.WaitForExitAsync();
            }
        }

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal([CustomLoopRunStoreStatus.Conflict.ToString(), CustomLoopRunStoreStatus.Updated.ToString()], new[] { await File.ReadAllTextAsync(firstResultPath), await File.ReadAllTextAsync(secondResultPath) }.Order(StringComparer.Ordinal));
        _ = await firstOutput;
        _ = await firstError;
        _ = await secondOutput;
        _ = await secondError;
        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(predecessor.Id));
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Contains(persisted.HumanReview?.Request.RequestId, new[] { "review-request-race-first", "review-request-race-second" });
        Assert.Equal(2, persisted.Events.Length - predecessor.Events.Length);
    }

    [Fact]
    public async Task Human_review_admission_rejects_a_valid_request_that_exceeds_reserved_trace_capacity_without_partial_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var current = await PersistRealRunningPredecessorAsync(paths);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, current.LoopId, current.Id + ".json");
        var predecessorBytes = await File.ReadAllBytesAsync(artifactPath);
        var transition = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(current.Frontier, current.SequentialAdapterBinding!, null, null, current.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, transition.Status);
        var blocked = Assert.IsType<GovernedLoopFrontierPosture>(transition.Frontier);
        var request = CreateHumanReviewRequest(current, blocked, current.UpdatedAtUtc, "quota-overflow") with
        {
            EligibleReviewers = Enumerable.Range(0, HumanReviewContractLimits.MaxEligibleReviewers)
                .Select(reviewer => new HumanReviewReviewerScope(
                    ("reviewer-" + reviewer.ToString("D2") + "-").PadRight(HumanReviewContractLimits.MaxIdentifierCharacters, 'a'),
                    Enumerable.Range(0, HumanReviewContractLimits.MaxScopesPerReviewer)
                        .Select(scope => ("scope-" + scope.ToString("D2") + "-").PadRight(HumanReviewContractLimits.MaxIdentifierCharacters, (char)('a' + reviewer)))
                        .ToImmutableArray()))
                .ToImmutableArray(),
            RequestHash = string.Empty
        };
        request = HumanReviewContractHash.ApplyRequest(request);
        Assert.True(HumanReviewContractValidator.ValidateRequest(request).IsValid);

        var exception = await Assert.ThrowsAsync<FormatException>(() => new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(current.Id, current.LifecycleVersion, request, blocked)));

        Assert.Contains("lifecycle control event exceeded its permanent reserved serialized footprint", exception.Message, StringComparison.Ordinal);
        Assert.Equal(predecessorBytes, await File.ReadAllBytesAsync(artifactPath));
        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(current.Id));
        Assert.Equal(current.LifecycleVersion, persisted.LifecycleVersion);
        Assert.Null(persisted.HumanReview);
        Assert.Equal(current.Frontier!.Payload.ContentHash, persisted.Frontier!.Payload.ContentHash);
    }

    [Fact]
    public async Task Human_review_admission_snapshots_caller_arrays_and_fresh_reads_ignore_mutated_returned_objects()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var store = new CustomLoopRunStore(paths);
        var predecessor = await PersistRealRunningPredecessorAsync(paths);
        var transition = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(predecessor.Frontier, predecessor.SequentialAdapterBinding!, null, null, predecessor.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, transition.Status);
        var blocked = Assert.IsType<GovernedLoopFrontierPosture>(transition.Frontier);
        var request = CreateHumanReviewRequest(predecessor, blocked, predecessor.UpdatedAtUtc, "defensive-copy");
        var decisionBacking = request.RequestedDecisions.ToArray();
        var reviewerScopeBacking = request.EligibleReviewers[0].ScopeIds.ToArray();
        var reviewerBacking = request.EligibleReviewers.ToArray();
        reviewerBacking[0] = reviewerBacking[0] with { ScopeIds = ImmutableCollectionsMarshal.AsImmutableArray(reviewerScopeBacking) };
        var previewBacking = request.Previews.ToArray();
        request = HumanReviewContractHash.ApplyRequest(request with
        {
            RequestedDecisions = ImmutableCollectionsMarshal.AsImmutableArray(decisionBacking),
            EligibleReviewers = ImmutableCollectionsMarshal.AsImmutableArray(reviewerBacking),
            Previews = ImmutableCollectionsMarshal.AsImmutableArray(previewBacking),
            RequestHash = string.Empty
        });
        var expectedRequestHash = request.RequestHash;

        var result = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(predecessor.Id, predecessor.LifecycleVersion, request, blocked));

        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        decisionBacking[0] = HumanReviewDecisionKind.Unknown;
        reviewerScopeBacking[0] = "mutated-scope";
        reviewerBacking[0] = new HumanReviewReviewerScope("mutated-role", ImmutableArray<string>.Empty);
        previewBacking[0] = previewBacking[0] with { Detail = "mutated", DetailHash = string.Empty };
        var returned = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal(expectedRequestHash, returned.HumanReview!.Request.RequestHash);
        Assert.True(HumanReviewContractValidator.ValidateRequest(returned.HumanReview.Request).IsValid);
        var expectedEvidenceHash = returned.HumanReview.Evidence.Single().EvidenceHash;
        returned.Events[^1] = returned.Events[^1] with { Detail = "mutated returned event" };
        var returnedEvidence = ImmutableCollectionsMarshal.AsArray(returned.HumanReview.Evidence)!;
        returnedEvidence[0] = returnedEvidence[0] with { EvidenceHash = HumanReviewHash('9') };

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(predecessor.Id));
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Equal(expectedRequestHash, persisted.HumanReview!.Request.RequestHash);
        Assert.Equal(expectedEvidenceHash, persisted.HumanReview.Evidence.Single().EvidenceHash);
        Assert.NotEqual("mutated returned event", persisted.Events[^1].Detail);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("evidence")]
    [InlineData("human-review-plane-omitted")]
    [InlineData("lifecycle-omitted")]
    [InlineData("evidence-omitted")]
    [InlineData("event-evidence-payload-omitted")]
    [InlineData("event-admission-marker-all-null")]
    [InlineData("binding-mismatch")]
    [InlineData("event-evidence-mismatch")]
    [InlineData("event-evidence-payload-mismatch")]
    public async Task Restart_rejects_incomplete_or_mismatched_human_review_admission_without_normalizing_it(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var persisted = await PersistHumanReviewAdmissionAsync(paths, "corruption-" + corruption);
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, persisted.LoopId, persisted.Id + ".json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(artifactPath))!.AsObject();
        var humanReview = root["run"]!["humanReview"]!.AsObject();
        switch (corruption)
        {
            case "request":
                humanReview["request"] = null;
                break;
            case "evidence":
                humanReview["evidence"]!.AsArray()[0] = null;
                break;
            case "human-review-plane-omitted":
                root["run"]!.AsObject().Remove("humanReview");
                break;
            case "lifecycle-omitted":
                humanReview.Remove("lifecycle");
                break;
            case "evidence-omitted":
                humanReview.Remove("evidence");
                break;
            case "event-evidence-payload-omitted":
                root["run"]!["events"]!.AsArray()[^1]!.AsObject().Remove("humanReviewEvidence");
                break;
            case "event-admission-marker-all-null":
                AppendNullHumanReviewEventMarker(root, "humanReviewRequestAdmitted", "orphan-human-review-admission");
                break;
            case "binding-mismatch":
                humanReview["request"]!["binding"]!["graphId"] = "other-graph";
                break;
            case "event-evidence-mismatch":
                root["run"]!["events"]!.AsArray()[^1]!.AsObject()["timestampUtc"] = persisted.UpdatedAtUtc.AddSeconds(-1).ToString("O");
                break;
            case "event-evidence-payload-mismatch":
                root["run"]!["events"]!.AsArray()[^1]!["humanReviewEvidence"]!["provenance"]!["sourceId"] = "mutated-source";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        await File.WriteAllTextAsync(artifactPath, root.ToJsonString() + "\n");
        using var restarted = new CustomLoopRunStore(paths);
        var exception = await Assert.ThrowsAsync<FormatException>(() => restarted.GetAsync(persisted.Id));
        if (corruption == "event-admission-marker-all-null") Assert.Contains("Each Human Review event must carry its exact retained evidence", exception.Message);
    }

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
        Assert.Collection(
            hydratedFrontier.Payload.Nodes,
            trigger =>
            {
                Assert.Equal(trigger.PlanOrdinal, trigger.ActivationOrdinal);
                Assert.Equal(1, trigger.VisitOrdinal);
                Assert.Null(trigger.CycleId);
                Assert.Null(trigger.CycleIteration);
                Assert.Equal(GovernedLoopControlCondition.Always, trigger.ControlOutcome);
                Assert.Equal(trigger.OutgoingControlEdgeIds, trigger.SelectedControlEdgeIds);
                Assert.Empty(trigger.SkippedControlEdgeIds);
                Assert.Empty(trigger.JoinArrivals);
                Assert.Equal(hydrated.Events[0].EventId, trigger.OutcomeEvidenceId);
                Assert.Equal(hydrated.Events[0].SequentialNodeEvidence!.OutcomeArtifactHash, trigger.OutcomeEvidenceHash);
            },
            inference =>
            {
                Assert.Equal(inference.PlanOrdinal, inference.ActivationOrdinal);
                Assert.Equal(1, inference.VisitOrdinal);
                Assert.Null(inference.CycleId);
                Assert.Null(inference.CycleIteration);
                Assert.Null(inference.ControlOutcome);
                Assert.Empty(inference.SelectedControlEdgeIds);
                Assert.Empty(inference.SkippedControlEdgeIds);
                Assert.Empty(inference.JoinArrivals);
                Assert.Null(inference.OutcomeEvidenceId);
                Assert.Null(inference.OutcomeEvidenceHash);
            });

        var source = frontier.Payload.Nodes[1];
        var cycleActivation = GovernedLoopNodeExecutionEvidence.CreateActivation(
            source.ActivationOrdinal,
            source.PlanOrdinal,
            source.VisitOrdinal,
            source.NodeId,
            source.Descriptor,
            source.IncomingControlEdgeIds,
            source.OutgoingControlEdgeIds,
            source.Status,
            source.Attempt,
            source.AttemptOperationId,
            source.OutcomeEvidenceId,
            source.OutcomeEvidenceHash,
            "cycle-main",
            1,
            null,
            [],
            [],
            source.JoinArrivals);
        var enrichedFrontier = GovernedLoopFrontierPosture.Create(
            frontier.Binding,
            frontier.WorkspaceId,
            frontier.GraphArtifactHash,
            frontier.GraphLayoutHash,
            frontier.AdmissionReceiptHash,
            frontier.Payload.FrontierVersion,
            frontier.Payload.ConcurrencyCeiling,
            frontier.Payload.Status,
            [frontier.Payload.Nodes[0], cycleActivation],
            frontier.Payload.UpdatedAtUtc,
            string.Empty);
        var enriched = context.Run with { Frontier = enrichedFrontier };
        var hydratedEnriched = CustomLoopRunArtifactSerializer.Deserialize(CustomLoopRunArtifactSerializer.Serialize(enriched));
        var enrichedActivation = Assert.IsType<GovernedLoopFrontierPosture>(hydratedEnriched.Frontier).Payload.Nodes[1];
        Assert.Equal("cycle-main", enrichedActivation.CycleId);
        Assert.Equal(1, enrichedActivation.CycleIteration);
        Assert.Null(enrichedActivation.ControlOutcome);
        Assert.Empty(enrichedActivation.SelectedControlEdgeIds);
        Assert.Empty(enrichedActivation.SkippedControlEdgeIds);

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
            running.Frontier!.Payload.Nodes[1],
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            running.UpdatedAtUtc.AddMinutes(1));
        var advanced = await UpdateAndRestartAsync(paths, CompleteInference(running, context, completion), 3);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, advanced.Frontier!.Payload.Nodes[1].Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, advanced.Frontier.Payload.Nodes[2].Status);
        AssertExactOutcomeEvidence(advanced, nodeIndex: 1);

        var exitRunning = await UpdateAndRestartAsync(paths, StartExit(advanced, context, "event-exit-start"), 4);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, exitRunning.Frontier!.Payload.Nodes[2].Status);
        var completed = await UpdateAndRestartAsync(paths, CompleteExit(exitRunning, context, "event-exit-completed"), 5);
        Assert.Equal(CustomLoopRunStatus.Completed, completed.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Completed, completed.Frontier!.Payload.Status);
        Assert.All(completed.Frontier.Payload.Nodes, node => Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, node.Status));
        AssertExactOutcomeEvidence(completed, nodeIndex: 2);
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
        AssertExactOutcomeEvidence(loaded, nodeIndex: 1);
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
            running.Frontier!.Payload.Nodes[1],
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            running.UpdatedAtUtc.AddMinutes(1));
        var secondCompletion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            "event-completed-b",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            context.Binding,
            context.Plan.Nodes[1],
            running.Frontier!.Payload.Nodes[1],
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
        AssertExactOutcomeEvidence(winner, nodeIndex: 1);
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
            running.Frontier!.Payload.Nodes[1],
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
        AssertExactOutcomeEvidence(loaded, nodeIndex: 1);
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
            running.Frontier!.Payload.Nodes[1],
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

    private static async Task<CustomLoopRunRecord> PersistRealRunningPredecessorAsync(WorkspacePaths paths)
    {
        var context = CustomLoopSequentialEvidenceStoreTests.CreateContext(identity: "human-review-process-loss");
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var active = TransitionToRunning(context.Run, "human-review-process-loss-running");
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(active, context.Run.LifecycleVersion)).Status);
        var running = StartInference(active, context, "human-review-process-loss-start");
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, active.LifecycleVersion)).Status);
        return running;
    }

    [Fact]
    public async Task Post_admission_decision_state_matrix_round_trips_through_the_real_store_and_restart()
    {
        foreach (var scenario in new[]
        {
            HumanReviewDecisionScenario.RequestInformation,
            HumanReviewDecisionScenario.Approve,
            HumanReviewDecisionScenario.Reject,
            HumanReviewDecisionScenario.Cancel,
            HumanReviewDecisionScenario.Denied,
            HumanReviewDecisionScenario.Conflict,
            HumanReviewDecisionScenario.Expired,
        })
        {
            using var workspace = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);
            var admitted = await PersistHumanReviewAdmissionAsync(paths, "decision-" + scenario.ToString().ToLowerInvariant());
            var candidate = CreateDecisionState(admitted, scenario);

            Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
            using (var store = new CustomLoopRunStore(paths))
            {
                var stored = await store.UpdateAsync(candidate, admitted.LifecycleVersion);
                Assert.Equal(CustomLoopRunStoreStatus.Updated, stored.Status);
            }

            using var restarted = new CustomLoopRunStore(paths);
            var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(admitted.Id));
            Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
            Assert.Equal(CustomLoopRunArtifactSerializer.Serialize(candidate), CustomLoopRunArtifactSerializer.Serialize(persisted));
            Assert.Equal(scenario == HumanReviewDecisionScenario.Approve, persisted.HumanReview!.ContinuationReservation is not null);
        }
    }

    [Fact]
    public async Task Accepted_approval_retains_a_later_conflict_audit_without_reopening_the_terminal_lifecycle()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await PersistHumanReviewAdmissionAsync(paths, "approval-conflict-audit");
        var approval = CreateDecisionState(admitted, HumanReviewDecisionScenario.Approve);

        CustomLoopRunRecord approved;
        using (var store = new CustomLoopRunStore(paths))
        {
            var stored = await store.UpdateAsync(approval, admitted.LifecycleVersion);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, stored.Status);
            approved = Assert.IsType<CustomLoopRunRecord>(stored.Run);
        }

        var candidate = CreateDecisionState(approved, HumanReviewDecisionScenario.Conflict);
        Assert.True(CustomLoopRunValidator.ValidateUpdate(approved, candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(approved, candidate).Errors));
        Assert.True(CustomLoopRunValidator.HasExactDurableEventPrefix(approved, candidate));
        Assert.Equal(approved.HumanReview!.Lifecycle.LifecycleHash, candidate.HumanReview!.Lifecycle.LifecycleHash);
        Assert.Equal(approved.HumanReview.LifecycleHistory.Select(item => item.LifecycleHash), candidate.HumanReview.LifecycleHistory.Select(item => item.LifecycleHash));
        Assert.Equal(approved.HumanReview.ContinuationReservation!.ReservationHash, candidate.HumanReview.ContinuationReservation!.ReservationHash);
        Assert.Equal(
            [HumanReviewDecisionOperationDisposition.Accepted, HumanReviewDecisionOperationDisposition.Conflict],
            candidate.HumanReview.OperationReceipts.Select(receipt => receipt.Disposition));
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(candidate, approved.LifecycleVersion)).Status);
        }
        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(candidate.Id));
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Equal(HumanReviewLifecycleStatus.Approved, persisted.HumanReview!.Lifecycle.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, persisted.HumanReview.OperationReceipts[^1].Disposition);
    }

    [Fact]
    public async Task Two_distinct_expired_operations_share_one_expired_lifecycle_across_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await PersistHumanReviewAdmissionAsync(paths, "two-expired-operations");
        var candidate = CreateDecisionState(admitted, HumanReviewDecisionScenario.Expired, HumanReviewDecisionScenario.Expired);

        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        Assert.Equal(2, candidate.HumanReview!.OperationReceipts.Length);
        Assert.Equal(2, candidate.HumanReview.OperationReceipts.Select(receipt => receipt.DecisionOperationId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(candidate.HumanReview.OperationReceipts, receipt => Assert.Equal(HumanReviewDecisionOperationDisposition.Expired, receipt.Disposition));
        Assert.Equal(2, candidate.HumanReview.LifecycleHistory.Length);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, candidate.HumanReview.Lifecycle.Status);
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(candidate, admitted.LifecycleVersion)).Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(candidate.Id));
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Equal(2, persisted.HumanReview!.OperationReceipts.Length);
        Assert.Equal(2, persisted.HumanReview.LifecycleHistory.Length);
    }

    [Fact]
    public async Task Lifecycle_history_reserves_the_expiry_slot_after_the_maximum_accepted_information_requests()
    {
        using var workspace = new TestWorkspace();
        var admitted = await PersistHumanReviewAdmissionAsync(new WorkspacePaths(workspace.RootPath), "lifecycle-history-boundary");
        var atLimit = CreateDecisionState(admitted, [.. Enumerable.Repeat(HumanReviewDecisionScenario.RequestInformation, HumanReviewContractLimits.MaxAcceptedDecisions), HumanReviewDecisionScenario.Expired]);
        var overLimit = CreateDecisionState(admitted, [.. Enumerable.Repeat(HumanReviewDecisionScenario.RequestInformation, HumanReviewContractLimits.MaxAcceptedDecisions + 1), HumanReviewDecisionScenario.Expired]);
        var overLimitValidation = CustomLoopRunValidator.Validate(overLimit);

        Assert.Equal(HumanReviewContractLimits.MaxLifecycleHistory, atLimit.HumanReview!.LifecycleHistory.Length);
        Assert.True(CustomLoopRunValidator.Validate(atLimit).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(atLimit).Errors));
        Assert.Equal(HumanReviewContractLimits.MaxLifecycleHistory + 1, overLimit.HumanReview!.LifecycleHistory.Length);
        Assert.Contains(overLimitValidation.Errors, error => error.Code == "human_review_lifecycle_history_limit");
    }

    [Fact]
    public async Task Decision_state_rejects_cross_plane_substitution_duplicate_identity_and_terminal_history_rewrites()
    {
        using var workspace = new TestWorkspace();
        var admitted = await PersistHumanReviewAdmissionAsync(new WorkspacePaths(workspace.RootPath), "decision-invalid");
        var approved = CreateDecisionState(admitted, HumanReviewDecisionScenario.Approve);
        var approvedState = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var approval = Assert.IsType<HumanReviewDecision>(approvedState.AcceptedTerminalDecision);
        var receipt = approvedState.OperationReceipts.Single();
        var copiedRejectAsApproval = HumanReviewContractHash.ApplyDecision(HumanReviewTestDataDecision(approvedState.Request, HumanReviewDecisionKind.Reject, "copied-terminal", "copied-operation", approved.UpdatedAtUtc.AddSeconds(-4))) with
        {
            Kind = HumanReviewDecisionKind.Approve,
            DecisionHash = HumanReviewHash('9'),
        };
        var terminalSubstitution = approved with
        {
            HumanReview = approvedState with
            {
                AcceptedTerminalDecision = copiedRejectAsApproval,
                ContinuationReservation = approvedState.ContinuationReservation! with { Decision = new HumanReviewDecisionReference(copiedRejectAsApproval.DecisionId, copiedRejectAsApproval.DecisionOperationId, copiedRejectAsApproval.Kind, copiedRejectAsApproval.DecisionHash) },
            },
        };
        var duplicateEvidence = approved with { HumanReview = approvedState with { Evidence = [approvedState.Evidence[0], approvedState.Evidence[0], .. approvedState.Evidence.Skip(2)] } };
        var duplicateReceipt = approved with { HumanReview = approvedState with { OperationReceipts = [receipt, receipt] } };
        var orderedLedger = CreateDecisionState(admitted, HumanReviewDecisionScenario.RequestInformation, HumanReviewDecisionScenario.Conflict);
        var orderedLedgerState = Assert.IsType<HumanReviewRunState>(orderedLedger.HumanReview);
        var reorderedReceiptLedger = orderedLedger with { HumanReview = orderedLedgerState with { OperationReceipts = [orderedLedgerState.OperationReceipts[1], orderedLedgerState.OperationReceipts[0]] } };
        var informationAfterTerminal = CreateDecisionState(admitted, HumanReviewDecisionScenario.Reject, HumanReviewDecisionScenario.RequestInformation);
        var informationAfterApproval = CreateDecisionState(admitted, HumanReviewDecisionScenario.Approve, HumanReviewDecisionScenario.RequestInformation);
        var mismatchedEvidenceDisposition = approved with
        {
            HumanReview = approvedState with
            {
                Evidence = [approvedState.Evidence[0], HumanReviewContractHash.ApplyEvidence(approvedState.Evidence[1] with { Kind = HumanReviewEvidenceKind.InformationRequested, EvidenceHash = string.Empty }), .. approvedState.Evidence.Skip(2)],
            },
        };
        var earlyLifecycleAtUtc = approvedState.Evidence[1].RecordedAtUtc.AddTicks(-1);
        var earlyLifecycle = HumanReviewContractHash.ApplyLifecycle(approvedState.Lifecycle with
        {
            UpdatedAtUtc = earlyLifecycleAtUtc,
            Provenance = HumanReviewContractHash.ApplyProvenance(approvedState.Lifecycle.Provenance with { ObservedAtUtc = earlyLifecycleAtUtc, ProvenanceHash = string.Empty }),
            LifecycleHash = string.Empty,
        });
        var lifecycleBeforeEvidence = approved with { HumanReview = approvedState with { Lifecycle = earlyLifecycle, LifecycleHistory = [approvedState.LifecycleHistory[0], earlyLifecycle] } };
        var defaultReceiptArray = approved with { HumanReview = approvedState with { OperationReceipts = default } };
        var defaultEvidenceArray = approved with { HumanReview = approvedState with { Evidence = default } };
        var combinedMalformedState = approved with
        {
            HumanReview = approvedState with
            {
                Lifecycle = null!,
                LifecycleHistory = default,
                OperationReceipts = default,
                AcceptedDecisions = default,
            },
        };
        var allDefaultMalformedState = combinedMalformedState with { HumanReview = combinedMalformedState.HumanReview! with { Evidence = default } };
        var operationOnAdmissionEvidence = approved with
        {
            HumanReview = approvedState with
            {
                Evidence = [HumanReviewContractHash.ApplyEvidence(approvedState.Evidence[0] with { DecisionOperation = approvedState.Evidence[1].DecisionOperation, EvidenceHash = string.Empty }), .. approvedState.Evidence.Skip(1)],
            },
        };
        var eventWithoutEvidence = approved with { Events = [.. approved.Events, approved.Events[^1] with { Sequence = approved.Events[^1].Sequence + 1, EventId = "orphan-review-reference", HumanReviewEvidence = null }] };

        Assert.True(CustomLoopRunValidator.Validate(approved).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(approved).Errors));
        Assert.False(CustomLoopRunValidator.Validate(terminalSubstitution).IsValid);
        Assert.False(CustomLoopRunValidator.Validate(duplicateEvidence).IsValid);
        Assert.False(CustomLoopRunValidator.Validate(duplicateReceipt).IsValid);
        Assert.Contains(CustomLoopRunValidator.Validate(reorderedReceiptLedger).Errors, error => error.Code == "human_review_receipt_evidence_order");
        Assert.False(CustomLoopRunValidator.Validate(informationAfterTerminal).IsValid);
        Assert.False(CustomLoopRunValidator.Validate(informationAfterApproval).IsValid);
        Assert.False(CustomLoopRunValidator.Validate(mismatchedEvidenceDisposition).IsValid);
        Assert.False(CustomLoopRunValidator.Validate(lifecycleBeforeEvidence).IsValid);
        Assert.False(CustomLoopRunValidator.Validate(defaultReceiptArray).IsValid);
        Assert.Null(Record.Exception(() => CustomLoopRunValidator.Validate(defaultEvidenceArray)));
        Assert.False(CustomLoopRunValidator.Validate(defaultEvidenceArray).IsValid);
        Assert.Null(Record.Exception(() => CustomLoopRunValidator.Validate(combinedMalformedState)));
        Assert.False(CustomLoopRunValidator.Validate(combinedMalformedState).IsValid);
        Assert.Null(Record.Exception(() => CustomLoopRunValidator.Validate(allDefaultMalformedState)));
        Assert.False(CustomLoopRunValidator.Validate(allDefaultMalformedState).IsValid);
        using (var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath)))
        {
            await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(combinedMalformedState, admitted.LifecycleVersion));
        }
        Assert.False(CustomLoopRunValidator.Validate(operationOnAdmissionEvidence).IsValid);
        Assert.False(CustomLoopRunValidator.Validate(eventWithoutEvidence).IsValid);
        Assert.NotEqual(approval.DecisionHash, copiedRejectAsApproval.DecisionHash);
    }

    [Fact]
    public async Task Decision_state_requires_exact_durable_versions_and_allows_only_valid_later_prefixes()
    {
        using var workspace = new TestWorkspace();
        var admitted = await PersistHumanReviewAdmissionAsync(new WorkspacePaths(workspace.RootPath), "decision-prefix");
        var prefix = CreateDecisionState(admitted, HumanReviewDecisionScenario.RequestInformation);
        var laterBase = CreateDecisionState(admitted, HumanReviewDecisionScenario.RequestInformation, HumanReviewDecisionScenario.Conflict);
        var later = laterBase with { LifecycleVersion = prefix.LifecycleVersion + 1 };
        var state = Assert.IsType<HumanReviewRunState>(prefix.HumanReview);
        var substituted = prefix with
        {
            HumanReview = state with
            {
                OperationReceipts = [HumanReviewContractHash.ApplyDecisionOperationReceipt(state.OperationReceipts[0] with { ProposalHash = HumanReviewHash('9'), ReceiptHash = string.Empty })],
            },
        };
        var eventSubstitution = prefix with
        {
            Events = [.. prefix.Events.Take(prefix.Events.Length - 1), prefix.Events[^1] with { HumanReviewDecisionOperation = new HumanReviewDecisionOperationReference("substituted-operation", HumanReviewHash('a'), HumanReviewDecisionOperationDisposition.InformationRequested, HumanReviewHash('b')) }],
        };

        Assert.True(CustomLoopRunValidator.Validate(prefix).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(prefix).Errors));
        Assert.True(CustomLoopRunValidator.Validate(later).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(later).Errors));
        Assert.True(CustomLoopRunValidator.HasSameDurableVersion(prefix, prefix with { Events = prefix.Events.Select(item => item with { ContextBlocks = [.. item.ContextBlocks] }).ToArray() }));
        Assert.True(CustomLoopRunValidator.HasExactDurableEventPrefix(prefix, later));
        Assert.False(CustomLoopRunValidator.HasSameDurableVersion(prefix, substituted));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(prefix, substituted));
        Assert.False(CustomLoopRunValidator.HasSameDurableVersion(prefix, eventSubstitution));
    }

    [Theory]
    [InlineData("lifecycle-history-null")]
    [InlineData("lifecycle-history-item-null")]
    [InlineData("receipts-null")]
    [InlineData("accepted-decisions-null")]
    [InlineData("evidence-null")]
    [InlineData("terminal-omitted")]
    [InlineData("reservation-omitted")]
    [InlineData("operation-reference-omitted")]
    [InlineData("reservation-reference-omitted")]
    [InlineData("event-operation-reference-omitted")]
    [InlineData("event-reservation-reference-omitted")]
    [InlineData("event-decision-marker-all-null")]
    [InlineData("event-reservation-marker-all-null")]
    [InlineData("unknown-receipt-property")]
    [InlineData("reordered-receipt")]
    [InlineData("receipt-hash")]
    public async Task Restart_rejects_missing_null_corrupt_or_reordered_decision_state_fields(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var identity = corruption switch
        {
            "event-operation-reference-omitted" => "event-operation-reference",
            "event-reservation-reference-omitted" => "event-reservation-reference",
            _ => "decision-codec-" + corruption,
        };
        var admitted = await PersistHumanReviewAdmissionAsync(paths, identity);
        var candidate = CreateDecisionState(admitted, HumanReviewDecisionScenario.Approve);
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(candidate, admitted.LifecycleVersion)).Status);
        }

        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, candidate.LoopId, candidate.Id + ".json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(artifactPath))!.AsObject();
        var state = root["run"]!["humanReview"]!.AsObject();
        switch (corruption)
        {
            case "lifecycle-history-null": state["lifecycleHistory"] = null; break;
            case "lifecycle-history-item-null": state["lifecycleHistory"] = new JsonArray((JsonNode?)null); break;
            case "receipts-null": state["operationReceipts"] = null; break;
            case "accepted-decisions-null": state["acceptedDecisions"] = null; break;
            case "evidence-null": state["evidence"] = null; break;
            case "terminal-omitted": state.Remove("acceptedTerminalDecision"); break;
            case "reservation-omitted": state.Remove("continuationReservation"); break;
            case "operation-reference-omitted": state["evidence"]!.AsArray()[1]!.AsObject().Remove("decisionOperation"); break;
            case "reservation-reference-omitted": state["evidence"]!.AsArray()[2]!.AsObject().Remove("continuationReservation"); break;
            case "event-operation-reference-omitted": root["run"]!["events"]!.AsArray().Single(item => item!["humanReviewDecisionOperation"] is not null)!.AsObject().Remove("humanReviewDecisionOperation"); break;
            case "event-reservation-reference-omitted": root["run"]!["events"]!.AsArray().Single(item => item!["humanReviewContinuationReservation"] is not null)!.AsObject().Remove("humanReviewContinuationReservation"); break;
            case "event-decision-marker-all-null": AppendNullHumanReviewEventMarker(root, "humanReviewDecisionOperationRecorded", "orphan-human-review-decision"); break;
            case "event-reservation-marker-all-null": AppendNullHumanReviewEventMarker(root, "humanReviewContinuationReserved", "orphan-human-review-reservation"); break;
            case "unknown-receipt-property": state["operationReceipts"]!.AsArray()[0]!["unexpected"] = true; break;
            case "reordered-receipt": state["operationReceipts"]![0] = ReverseProperties(state["operationReceipts"]!.AsArray()[0]!.AsObject()); break;
            case "receipt-hash": state["operationReceipts"]!.AsArray()[0]!["receiptHash"] = HumanReviewHash('9'); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        await File.WriteAllTextAsync(artifactPath, root.ToJsonString() + "\n");
        using var restarted = new CustomLoopRunStore(paths);
        var exception = await Assert.ThrowsAsync<FormatException>(() => restarted.GetAsync(candidate.Id));
        if (corruption.EndsWith("marker-all-null", StringComparison.Ordinal)) Assert.Contains("Each Human Review event must carry its exact retained evidence", exception.Message);
    }

    [Fact]
    public async Task Initial_admission_requires_every_decision_state_property_but_retains_explicit_null_terminal_and_reservation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await PersistHumanReviewAdmissionAsync(paths, "decision-required-properties");
        var encoded = CustomLoopRunArtifactSerializer.Serialize(admitted);
        var root = JsonNode.Parse(encoded)!.AsObject();
        var state = root["run"]!["humanReview"]!.AsObject();

        Assert.NotNull(state["lifecycleHistory"]);
        Assert.NotNull(state["operationReceipts"]);
        Assert.NotNull(state["acceptedDecisions"]);
        Assert.Null(state["acceptedTerminalDecision"]);
        Assert.Null(state["continuationReservation"]);
        Assert.Equal(encoded, CustomLoopRunArtifactSerializer.Serialize(CustomLoopRunArtifactSerializer.Deserialize(encoded)));

        foreach (var property in new[] { "evidence", "lifecycleHistory", "operationReceipts", "acceptedDecisions", "acceptedTerminalDecision", "continuationReservation" })
        {
            var omitted = JsonNode.Parse(encoded)!.AsObject();
            Assert.True(omitted["run"]!["humanReview"]!.AsObject().Remove(property));
            Assert.Throws<FormatException>(() => Deserialize(omitted));
        }

        foreach (var property in new[] { "evidence", "lifecycleHistory", "operationReceipts", "acceptedDecisions" })
        {
            var explicitNull = JsonNode.Parse(encoded)!.AsObject();
            explicitNull["run"]!["humanReview"]![property] = null;
            Assert.Throws<FormatException>(() => Deserialize(explicitNull));
        }
    }

    [Fact]
    public async Task Decision_state_snapshots_hostile_immutable_arrays_and_returned_buffers_across_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await PersistHumanReviewAdmissionAsync(paths, "decision-defensive");
        var candidate = CreateDecisionState(admitted, HumanReviewDecisionScenario.Approve);
        var state = Assert.IsType<HumanReviewRunState>(candidate.HumanReview);
        var receiptBacking = state.OperationReceipts.ToArray();
        var decisionBacking = state.AcceptedDecisions.ToArray();
        var evidenceBacking = state.Evidence.ToArray();
        var lifecycleHistoryBacking = state.LifecycleHistory.ToArray();
        candidate = candidate with
        {
            HumanReview = state with
            {
                OperationReceipts = ImmutableCollectionsMarshal.AsImmutableArray(receiptBacking),
                AcceptedDecisions = ImmutableCollectionsMarshal.AsImmutableArray(decisionBacking),
                Evidence = ImmutableCollectionsMarshal.AsImmutableArray(evidenceBacking),
                LifecycleHistory = ImmutableCollectionsMarshal.AsImmutableArray(lifecycleHistoryBacking),
            },
        };
        var expectedReceiptHash = candidate.HumanReview.OperationReceipts[0].ReceiptHash;
        var expectedDecisionHash = candidate.HumanReview.AcceptedDecisions[0].DecisionHash;
        var expectedEvidenceHash = candidate.HumanReview.Evidence[^1].EvidenceHash;
        var expectedLifecycleHash = candidate.HumanReview.LifecycleHistory[^1].LifecycleHash;
        using (var store = new CustomLoopRunStore(paths))
        {
            var updated = await store.UpdateAsync(candidate, admitted.LifecycleVersion);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, updated.Status);
            var returned = Assert.IsType<CustomLoopRunRecord>(updated.Run);
            var returnedState = Assert.IsType<HumanReviewRunState>(returned.HumanReview);
            ImmutableCollectionsMarshal.AsArray(returnedState.OperationReceipts)![0] = returnedState.OperationReceipts[0] with { ReceiptHash = HumanReviewHash('9') };
            ImmutableCollectionsMarshal.AsArray(returnedState.AcceptedDecisions)![0] = returnedState.AcceptedDecisions[0] with { DecisionHash = HumanReviewHash('9') };
            ImmutableCollectionsMarshal.AsArray(returnedState.Evidence)![^1] = returnedState.Evidence[^1] with { EvidenceHash = HumanReviewHash('9') };
            ImmutableCollectionsMarshal.AsArray(returnedState.LifecycleHistory)![^1] = returnedState.LifecycleHistory[^1] with { LifecycleHash = HumanReviewHash('9') };
        }
        receiptBacking[0] = receiptBacking[0] with { ReceiptHash = HumanReviewHash('8') };
        decisionBacking[0] = decisionBacking[0] with { DecisionHash = HumanReviewHash('8') };
        evidenceBacking[^1] = evidenceBacking[^1] with { EvidenceHash = HumanReviewHash('8') };
        lifecycleHistoryBacking[^1] = lifecycleHistoryBacking[^1] with { LifecycleHash = HumanReviewHash('8') };

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(candidate.Id));
        Assert.Equal(expectedReceiptHash, persisted.HumanReview!.OperationReceipts[0].ReceiptHash);
        Assert.Equal(expectedDecisionHash, persisted.HumanReview.AcceptedDecisions[0].DecisionHash);
        Assert.Equal(expectedEvidenceHash, persisted.HumanReview.Evidence[^1].EvidenceHash);
        Assert.Equal(expectedLifecycleHash, persisted.HumanReview.LifecycleHistory[^1].LifecycleHash);
    }

    private static async Task<CustomLoopRunRecord> PersistHumanReviewAdmissionAsync(WorkspacePaths paths, string identity)
    {
        var predecessor = await PersistRealRunningPredecessorAsync(paths);
        var transition = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(predecessor.Frontier, predecessor.SequentialAdapterBinding, null, null, predecessor.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, transition.Status);
        var blocked = Assert.IsType<GovernedLoopFrontierPosture>(transition.Frontier);
        var request = CreateHumanReviewRequest(predecessor, blocked, predecessor.UpdatedAtUtc, identity);
        using var store = new CustomLoopRunStore(paths);
        var result = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(predecessor.Id, predecessor.LifecycleVersion, request, blocked));
        return Assert.IsType<CustomLoopRunRecord>(result.Run);
    }

    private static CustomLoopRunRecord CreateDecisionState(CustomLoopRunRecord admitted, params HumanReviewDecisionScenario[] scenarios)
    {
        var state = Assert.IsType<HumanReviewRunState>(admitted.HumanReview);
        var request = state.Request;
        var requestReference = new HumanReviewRequestReference(request.RequestId, request.RequestHash);
        var evidence = state.Evidence.ToList();
        var receipts = state.OperationReceipts.ToList();
        var decisions = state.AcceptedDecisions.ToList();
        var lifecycleHistory = state.LifecycleHistory.ToList();
        var events = admitted.Events.ToList();
        var timestamp = admitted.UpdatedAtUtc;
        var terminal = state.AcceptedTerminalDecision;
        var reservation = state.ContinuationReservation;

        for (var index = 0; index < scenarios.Length; index++)
        {
            var scenario = scenarios[index];
            timestamp = scenario == HumanReviewDecisionScenario.Expired && timestamp < request.Timing.ExpiresAtUtc
                ? request.Timing.ExpiresAtUtc
                : timestamp.AddMinutes(1);
            var operationNumber = receipts.Count + 1;
            var decisionKind = ScenarioDecisionKind(scenario);
            var decision = decisionKind is null ? null : HumanReviewTestDataDecision(request, decisionKind.Value, "decision-" + operationNumber, "decision-operation-" + operationNumber, timestamp);
            var proposal = HumanReviewContractHash.ApplyDecisionProposal(new HumanReviewDecisionProposal(
                HumanReviewDecisionProposal.CurrentSchemaVersion,
                "decision-operation-" + operationNumber,
                decisionKind ?? HumanReviewDecisionKind.Approve,
                decisionKind == HumanReviewDecisionKind.RequestInformation ? "A redacted clarification is required." : null,
                string.Empty));
            var disposition = ScenarioDisposition(scenario);
            var receipt = HumanReviewContractHash.ApplyDecisionOperationReceipt(new HumanReviewDecisionOperationReceipt(
                HumanReviewDecisionOperationReceipt.CurrentSchemaVersion,
                proposal.DecisionOperationId,
                proposal.ProposalHash,
                requestReference,
                disposition,
                decision is null ? null : DecisionReference(decision),
                timestamp,
                HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-decision-store", "receipt-" + operationNumber, timestamp, string.Empty)),
                string.Empty));
            receipts.Add(receipt);
            var operation = new HumanReviewDecisionOperationReference(receipt.DecisionOperationId, receipt.ProposalHash, receipt.Disposition, receipt.ReceiptHash);
            var evidenceKind = ScenarioEvidenceKind(scenario);
            var decisionEvidence = HumanReviewContractHash.ApplyEvidence(new HumanReviewEvidence(
                HumanReviewEvidence.CurrentSchemaVersion,
                "decision-evidence-" + operationNumber,
                requestReference,
                evidenceKind,
                decision is null ? null : DecisionReference(decision),
                timestamp,
                HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-decision-store", "evidence-" + operationNumber, timestamp, string.Empty)),
                ImmutableArray<HumanReviewRedactedPreview>.Empty,
                evidence[^1].EvidenceHash,
                string.Empty)
            {
                DecisionOperation = operation,
            });
            evidence.Add(decisionEvidence);
            events.Add(CreateHumanReviewStateEvent(events[^1], decisionEvidence, operation, null));

            if (decision is not null)
            {
                decisions.Add(decision);
                if (decision.Kind != HumanReviewDecisionKind.RequestInformation)
                {
                    terminal ??= decision;
                }

                lifecycleHistory.Add(CreateDecisionLifecycle(requestReference, lifecycleHistory[^1], decision, timestamp));
            }
            else if (scenario == HumanReviewDecisionScenario.Expired && terminal is null && lifecycleHistory.All(item => item.Status != HumanReviewLifecycleStatus.Expired))
            {
                lifecycleHistory.Add(CreateExpiredLifecycle(requestReference, lifecycleHistory[^1], timestamp));
            }
        }

        if (terminal?.Kind == HumanReviewDecisionKind.Approve && reservation is null)
        {
            timestamp = timestamp.AddMinutes(1);
            reservation = HumanReviewContractHash.ApplyContinuationReservation(new HumanReviewContinuationReservation(
                HumanReviewContinuationReservation.CurrentSchemaVersion,
                "continuation-reservation-one",
                requestReference,
                DecisionReference(terminal),
                timestamp,
                HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-decision-store", "reservation-one", timestamp, string.Empty)),
                string.Empty));
            var reservationReference = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash);
            var reservationEvidence = HumanReviewContractHash.ApplyEvidence(new HumanReviewEvidence(
                HumanReviewEvidence.CurrentSchemaVersion,
                "reservation-evidence-one",
                requestReference,
                HumanReviewEvidenceKind.ContinuationReserved,
                DecisionReference(terminal),
                timestamp,
                HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-decision-store", "reservation-evidence-one", timestamp, string.Empty)),
                ImmutableArray<HumanReviewRedactedPreview>.Empty,
                evidence[^1].EvidenceHash,
                string.Empty)
            {
                ContinuationReservation = reservationReference,
            });
            evidence.Add(reservationEvidence);
            events.Add(CreateHumanReviewStateEvent(events[^1], reservationEvidence, null, reservationReference));
        }

        foreach (var item in evidence)
        {
            Assert.True(HumanReviewContractValidator.ValidateEvidence(request, item).IsValid, item.Kind + ": " + string.Join(Environment.NewLine, HumanReviewContractValidator.ValidateEvidence(request, item).Errors));
        }

        return admitted with
        {
            LifecycleVersion = admitted.LifecycleVersion + 1,
            UpdatedAtUtc = timestamp,
            HumanReview = state with
            {
                Lifecycle = lifecycleHistory[^1],
                LifecycleHistory = lifecycleHistory.ToImmutableArray(),
                OperationReceipts = receipts.ToImmutableArray(),
                AcceptedDecisions = decisions.ToImmutableArray(),
                AcceptedTerminalDecision = terminal,
                ContinuationReservation = reservation,
                Evidence = evidence.ToImmutableArray(),
            },
            Events = events.ToArray(),
        };
    }

    private static HumanReviewDecision HumanReviewTestDataDecision(HumanReviewRequest request, HumanReviewDecisionKind kind, string decisionId, string operationId, DateTimeOffset timestamp)
        => HumanReviewContractHash.ApplyDecision(new HumanReviewDecision(
            HumanReviewDecision.CurrentSchemaVersion,
            decisionId,
            operationId,
            new HumanReviewRequestReference(request.RequestId, request.RequestHash),
            kind,
            "reviewer-one",
            "reviewer-role-one",
            ImmutableArray.Create("scope-alpha", "scope-beta"),
            timestamp,
            kind == HumanReviewDecisionKind.RequestInformation ? "A redacted clarification is required." : null,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.AuthenticatedReviewer, "reviewer-one", decisionId, timestamp, string.Empty)),
            string.Empty));

    private static HumanReviewLifecycle CreateDecisionLifecycle(HumanReviewRequestReference request, HumanReviewLifecycle previous, HumanReviewDecision decision, DateTimeOffset timestamp)
    {
        var status = decision.Kind switch
        {
            HumanReviewDecisionKind.RequestInformation => HumanReviewLifecycleStatus.AwaitingInformation,
            HumanReviewDecisionKind.Approve => HumanReviewLifecycleStatus.Approved,
            HumanReviewDecisionKind.Reject => HumanReviewLifecycleStatus.Rejected,
            HumanReviewDecisionKind.Cancel => HumanReviewLifecycleStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        return HumanReviewContractHash.ApplyLifecycle(new HumanReviewLifecycle(
            HumanReviewLifecycle.CurrentSchemaVersion,
            request,
            status,
            previous.LifecycleVersion + 1,
            timestamp,
            DecisionReference(decision),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-decision-store", "lifecycle-" + decision.DecisionId, timestamp, string.Empty)),
            previous.LifecycleHash,
            string.Empty));
    }

    private static HumanReviewLifecycle CreateExpiredLifecycle(HumanReviewRequestReference request, HumanReviewLifecycle previous, DateTimeOffset timestamp)
        => HumanReviewContractHash.ApplyLifecycle(new HumanReviewLifecycle(
            HumanReviewLifecycle.CurrentSchemaVersion,
            request,
            HumanReviewLifecycleStatus.Expired,
            previous.LifecycleVersion + 1,
            timestamp,
            null,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-decision-store", "lifecycle-expired", timestamp, string.Empty)),
            previous.LifecycleHash,
            string.Empty));

    private static void AppendNullHumanReviewEventMarker(JsonObject root, string kind, string eventId)
    {
        var events = root["run"]!["events"]!.AsArray();
        var marker = events[^1]!.DeepClone().AsObject();
        marker["sequence"] = marker["sequence"]!.GetValue<long>() + 1;
        marker["eventId"] = eventId;
        marker["kind"] = kind;
        marker["humanReviewEvidence"] = null;
        marker["humanReviewDecisionOperation"] = null;
        marker["humanReviewContinuationReservation"] = null;
        events.Add(marker);
    }

    private static CustomLoopRunEvent CreateHumanReviewStateEvent(CustomLoopRunEvent previous, HumanReviewEvidence evidence, HumanReviewDecisionOperationReference? operation, HumanReviewContinuationReservationReference? reservation)
        => previous with
        {
            Sequence = previous.Sequence + 1,
            EventId = operation is null ? "event-" + reservation!.ReservationId : "event-" + operation.DecisionOperationId,
            TimestampUtc = evidence.RecordedAtUtc,
            Kind = operation is null ? CustomLoopRunEventKind.HumanReviewContinuationReserved : CustomLoopRunEventKind.HumanReviewDecisionOperationRecorded,
            Detail = operation is null ? "Human Review continuation was reserved." : "Human Review decision operation was recorded.",
            HumanReviewEvidence = evidence,
            HumanReviewDecisionOperation = operation,
            HumanReviewContinuationReservation = reservation,
        };

    private static HumanReviewDecisionReference DecisionReference(HumanReviewDecision decision)
        => new(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash);

    private static HumanReviewDecisionKind? ScenarioDecisionKind(HumanReviewDecisionScenario scenario)
        => scenario switch
        {
            HumanReviewDecisionScenario.RequestInformation => HumanReviewDecisionKind.RequestInformation,
            HumanReviewDecisionScenario.Approve => HumanReviewDecisionKind.Approve,
            HumanReviewDecisionScenario.Reject => HumanReviewDecisionKind.Reject,
            HumanReviewDecisionScenario.Cancel => HumanReviewDecisionKind.Cancel,
            _ => null,
        };

    private static HumanReviewDecisionOperationDisposition ScenarioDisposition(HumanReviewDecisionScenario scenario)
        => scenario switch
        {
            HumanReviewDecisionScenario.RequestInformation => HumanReviewDecisionOperationDisposition.InformationRequested,
            HumanReviewDecisionScenario.Approve or HumanReviewDecisionScenario.Reject or HumanReviewDecisionScenario.Cancel => HumanReviewDecisionOperationDisposition.Accepted,
            HumanReviewDecisionScenario.Denied => HumanReviewDecisionOperationDisposition.Denied,
            HumanReviewDecisionScenario.Conflict => HumanReviewDecisionOperationDisposition.Conflict,
            HumanReviewDecisionScenario.Expired => HumanReviewDecisionOperationDisposition.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static HumanReviewEvidenceKind ScenarioEvidenceKind(HumanReviewDecisionScenario scenario)
        => scenario switch
        {
            HumanReviewDecisionScenario.RequestInformation => HumanReviewEvidenceKind.InformationRequested,
            HumanReviewDecisionScenario.Approve or HumanReviewDecisionScenario.Reject or HumanReviewDecisionScenario.Cancel => HumanReviewEvidenceKind.DecisionAccepted,
            HumanReviewDecisionScenario.Denied => HumanReviewEvidenceKind.DecisionDenied,
            HumanReviewDecisionScenario.Conflict => HumanReviewEvidenceKind.DecisionConflict,
            HumanReviewDecisionScenario.Expired => HumanReviewEvidenceKind.DecisionExpired,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private enum HumanReviewDecisionScenario
    {
        RequestInformation,
        Approve,
        Reject,
        Cancel,
        Denied,
        Conflict,
        Expired,
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }

    private static HumanReviewRequest CreateHumanReviewRequest(
        CustomLoopRunRecord predecessor,
        GovernedLoopFrontierPosture blocked,
        DateTimeOffset createdAtUtc,
        string identity = "one")
    {
        var blockedNode = Assert.Single(blocked.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(
            1,
            blocked.WorkspaceId,
            predecessor.Id,
            blocked.Binding.Revision.GraphId,
            blocked.Binding.Revision.RevisionId,
            blocked.Binding.Revision.ExecutableHash,
            blockedNode.NodeId,
            blockedNode.ActivationOrdinal,
            null,
            blockedNode.Attempt!.Value,
            "frontier-review-" + identity,
            blocked.Payload.FrontierVersion,
            blocked.Payload.ContentHash,
            HumanReviewHash('a'),
            HumanReviewHash('b'),
            HumanReviewHash('c'),
            HumanReviewHash('d'),
            HumanReviewHash('e'),
            HumanReviewHash('f'),
            HumanReviewHash('1'),
            null,
            string.Empty));
        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(HumanReviewApprovalScopeKind.Continuation, binding.BindingHash, null, string.Empty));
        var timing = new HumanReviewTiming(createdAtUtc, createdAtUtc.AddMinutes(10), createdAtUtc.AddHours(1));
        return HumanReviewContractHash.ApplyRequest(new HumanReviewRequest(
            1,
            "review-request-" + identity,
            "review-request-operation-" + identity,
            binding,
            HumanReviewPurpose.Continuation,
            ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation),
            ImmutableArray.Create(new HumanReviewReviewerScope("reviewer-role-one", ImmutableArray.Create("scope-alpha", "scope-beta"))),
            scope,
            ImmutableArray.Create(
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "Redacted action.", string.Empty)),
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "Redacted result.", string.Empty)),
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "Redacted evidence.", string.Empty))),
            timing,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-store", "request-correlation-" + identity, createdAtUtc, string.Empty)),
            string.Empty));
    }

    private static string HumanReviewHash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

    private static CustomLoopRunRecord StartInference(
        CustomLoopRunRecord run,
        CustomLoopSequentialEvidenceStoreTests.SequentialContext context,
        string eventId)
    {
        var node = context.Plan.Nodes[1];
        var activation = run.Frontier!.Payload.Nodes[1];
        var updatedAtUtc = run.UpdatedAtUtc.AddMinutes(1);
        var start = CreateSequentialEvent(
            run.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.NodeAttemptStarted,
            context.Binding,
            node,
            activation,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            updatedAtUtc);
        var runningNode = GovernedLoopNodeExecutionEvidence.CreateActivation(
            activation.ActivationOrdinal,
            activation.PlanOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Descriptor,
            activation.IncomingControlEdgeIds,
            activation.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Running,
            1,
            start.EventId,
            cycleId: activation.CycleId,
            cycleIteration: activation.CycleIteration,
            joinArrivals: activation.JoinArrivals);
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
        var exit = context.Plan.Nodes[2];
        var activation = running.Frontier!.Payload.Nodes[1];
        var evidence = completion.SequentialNodeEvidence!;
        var completedNode = GovernedLoopNodeExecutionEvidence.CreateActivation(
            activation.ActivationOrdinal,
            activation.PlanOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Descriptor,
            activation.IncomingControlEdgeIds,
            activation.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            activation.AttemptOperationId,
            completion.EventId,
            evidence.OutcomeArtifactHash,
            activation.CycleId,
            activation.CycleIteration,
            evidence.ControlOutcome,
            evidence.SelectedControlEdgeIds,
            evidence.SkippedControlEdgeIds,
            activation.JoinArrivals);
        var readyExit = GovernedLoopNodeExecutionEvidence.CreateActivation(
            checked(completedNode.ActivationOrdinal + 1),
            exit.Ordinal,
            1,
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
        var activation = run.Frontier!.Payload.Nodes[2];
        var updatedAtUtc = run.UpdatedAtUtc.AddMinutes(1);
        var start = CreateSequentialEvent(
            run.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.ExitDecisionStarted,
            context.Binding,
            exit,
            activation,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            updatedAtUtc);
        var runningExit = GovernedLoopNodeExecutionEvidence.CreateActivation(
            activation.ActivationOrdinal,
            activation.PlanOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Descriptor,
            activation.IncomingControlEdgeIds,
            activation.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Running,
            1,
            start.EventId,
            cycleId: activation.CycleId,
            cycleIteration: activation.CycleIteration,
            joinArrivals: activation.JoinArrivals);
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
        var activation = running.Frontier!.Payload.Nodes[2];
        var outcomeAtUtc = running.UpdatedAtUtc.AddMinutes(1);
        var completion = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            eventId,
            CustomLoopRunEventKind.ExitDecisionCompleted,
            context.Binding,
            exit,
            activation,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            outcomeAtUtc);
        var evidence = completion.SequentialNodeEvidence!;
        var completedExit = GovernedLoopNodeExecutionEvidence.CreateActivation(
            activation.ActivationOrdinal,
            activation.PlanOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Descriptor,
            activation.IncomingControlEdgeIds,
            activation.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            activation.AttemptOperationId,
            completion.EventId,
            evidence.OutcomeArtifactHash,
            activation.CycleId,
            activation.CycleIteration,
            evidence.ControlOutcome,
            evidence.SelectedControlEdgeIds,
            evidence.SkippedControlEdgeIds,
            activation.JoinArrivals);
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
        var activation = running.Frontier!.Payload.Nodes[1];
        var dispatchStart = running.Events.Single(item => string.Equals(item.EventId, activation.AttemptOperationId, StringComparison.Ordinal));
        var outcomeAtUtc = running.UpdatedAtUtc.AddMinutes(1);
        var outcome = CreateSequentialEvent(
            running.Events[^1].Sequence + 1,
            runStatus == CustomLoopRunStatus.NeedsReview ? "event-review-required" : "event-failed",
            CustomLoopRunEventKind.NodeAttemptFailed,
            context.Binding,
            node,
            activation,
            evidenceKind,
            disposition,
            outcomeAtUtc,
            new GovernedLoopFailureEvidenceReference(dispatchStart.EventId, dispatchStart.SequentialNodeEvidence!.EvidenceHash));
        var evidence = outcome.SequentialNodeEvidence!;
        var outcomeNode = GovernedLoopNodeExecutionEvidence.CreateActivation(
            activation.ActivationOrdinal,
            activation.PlanOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Descriptor,
            activation.IncomingControlEdgeIds,
            activation.OutgoingControlEdgeIds,
            nodeStatus,
            1,
            activation.AttemptOperationId,
            outcome.EventId,
            evidence.OutcomeArtifactHash,
            activation.CycleId,
            activation.CycleIteration,
            evidence.ControlOutcome,
            evidence.SelectedControlEdgeIds,
            evidence.SkippedControlEdgeIds,
            activation.JoinArrivals);
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

    private static void AssertExactOutcomeEvidence(CustomLoopRunRecord run, int nodeIndex)
    {
        var node = run.Frontier!.Payload.Nodes[nodeIndex];
        var evidenceId = Assert.IsType<string>(node.OutcomeEvidenceId);
        var outcomeEvent = Assert.Single(
            run.Events,
            item => string.Equals(item.EventId, evidenceId, StringComparison.Ordinal));
        var evidence = Assert.IsType<CustomLoopSequentialNodeEvidence>(outcomeEvent.SequentialNodeEvidence);

        Assert.True(CustomLoopSequentialNodeEvidenceHash.Matches(evidence));
        Assert.Equal(node.ActivationOrdinal, evidence.ActivationOrdinal);
        Assert.Equal(node.VisitOrdinal, evidence.VisitOrdinal);
        Assert.Equal(node.NodeId, evidence.NodeId);
        Assert.Equal(node.Attempt, evidence.Attempt);
        Assert.Equal(node.CycleId, evidence.CycleId);
        Assert.Equal(node.CycleIteration, evidence.CycleIteration);
        Assert.Equal(node.ControlOutcome, evidence.ControlOutcome);
        Assert.Equal(node.SelectedControlEdgeIds, evidence.SelectedControlEdgeIds);
        Assert.Equal(node.SkippedControlEdgeIds, evidence.SkippedControlEdgeIds);
        Assert.Equal(node.OutcomeEvidenceHash, evidence.OutcomeArtifactHash);
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
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopSequentialNodeEvidenceKind evidenceKind,
        CustomLoopSequentialNodeDisposition disposition,
        DateTimeOffset timestampUtc,
        GovernedLoopFailureEvidenceReference? failureSource = null)
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
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted or CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention => (GovernedLoopControlCondition?)null,
            _ when node.Descriptor.Kind == GovernedLoopNodeKind.Trigger => GovernedLoopControlCondition.Always,
            _ when disposition == CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopControlCondition.Failure,
            _ => GovernedLoopControlCondition.Success,
        };
        IReadOnlyList<string> selectedControlEdgeIds = controlOutcome is GovernedLoopControlCondition.Always or GovernedLoopControlCondition.Success
            ? activation.OutgoingControlEdgeIds
            : [];
        IReadOnlyList<string> skippedControlEdgeIds = controlOutcome == GovernedLoopControlCondition.Failure
            ? activation.OutgoingControlEdgeIds
            : [];
        var failure = disposition is CustomLoopSequentialNodeDisposition.Rejected or CustomLoopSequentialNodeDisposition.NeedsReview
            ? GovernedLoopFailureEvidenceContract.Create(
                runEvent.EventId + "-failure",
                binding.WorkspaceId,
                binding.ExecutionBinding.RunId,
                binding.ExecutionBinding.Revision,
                binding.ExecutionBinding.ExecutionGeneration,
                activation.ActivationOrdinal,
                activation.VisitOrdinal,
                node.NodeId,
                1,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? GovernedLoopFailureClass.EvidenceIntegrityFailure
                    : GovernedLoopFailureClass.ValidationConfiguration,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? "persistence-fixture-review"
                    : "persistence-fixture-rejected",
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? GovernedLoopFailureSource.Evidence
                    : GovernedLoopFailureSource.Validation,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? GovernedLoopFailureEffectCertainty.Unknown
                    : GovernedLoopFailureEffectCertainty.NotApplicable,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? GovernedLoopFailureAuthorityPosture.Unknown
                    : GovernedLoopFailureAuthorityPosture.NotApplicable,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? GovernedLoopFailureHumanPosture.Unknown
                    : GovernedLoopFailureHumanPosture.None,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? GovernedLoopFailureRetrySafety.Unknown
                    : GovernedLoopFailureRetrySafety.NotRetryable,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview
                    ? GovernedLoopFailureSeverity.ReviewBlocked
                    : GovernedLoopFailureSeverity.Error,
                disposition == CustomLoopSequentialNodeDisposition.NeedsReview ? 1_000 : 700,
                [failureSource ?? throw new InvalidOperationException("Failed sequential fixture evidence requires an exact causal source.")],
                null,
                timestampUtc)
            : null;
        var outcomeEvent = runEvent with { FailureEvidence = failure };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            1,
            evidenceKind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            node.NodeId,
            1,
            activation.CycleId,
            activation.CycleIteration,
            controlOutcome,
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(outcomeEvent),
            string.Empty)
        {
            FailureEvidenceId = failure?.EvidenceId,
            FailureEvidenceHash = failure?.ContentHash,
        });
        return outcomeEvent with { SequentialNodeEvidence = evidence };
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
        Verification.CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(
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
