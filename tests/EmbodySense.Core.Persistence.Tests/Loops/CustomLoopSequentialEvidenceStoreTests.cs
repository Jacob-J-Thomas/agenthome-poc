using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Tests.Authority.Grants;
using EmbodySense.Core.Common.Tests;
using EmbodySense.Core.Common.Tests.Loops.Admission;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopSequentialEvidenceStoreTests
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string ScheduleTriggerCapabilityId = "org.embodysense/triggers/time";
    private const string ModelProfileCapabilityId = "org.embodysense/model-profile/codex";
    private static readonly DateTimeOffset _timestamp = new(2026, 8, 10, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Canonical_trigger_outcome_round_trips_and_recorder_replays_without_mutation()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);

        using var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var loaded = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(context.Run.Id));
        Assert.Equal(context.Invocation.ContentHash, loaded.SequentialInvocationSnapshot?.ContentHash);
        Assert.Equal(context.Binding.ContentHash, loaded.SequentialAdapterBinding?.ContentHash);
        Assert.Equal(context.Binding.AdmissionReceiptHash, loaded.SequentialAdapterBinding?.AdmissionReceiptHash);
        Assert.Equal(context.Binding.AdmissionReceipt.ContentHash, loaded.SequentialAdapterBinding?.AdmissionReceipt.ContentHash);
        Assert.NotSame(context.Binding.AdmissionReceipt, loaded.SequentialAdapterBinding?.AdmissionReceipt);
        Assert.Equal(context.Binding.AdmissionReceipt.Evidence.GrantProfile, loaded.SequentialAdapterBinding?.AdmissionReceipt.Evidence.GrantProfile);
        Assert.Equal(context.Binding.AdmissionReceipt.Evidence.GrantBoundary, loaded.SequentialAdapterBinding?.AdmissionReceipt.Evidence.GrantBoundary);
        Assert.Equal(context.Binding.AdmissionReceipt.Evidence.GrantDependencyEvidenceHash, loaded.SequentialAdapterBinding?.AdmissionReceipt.Evidence.GrantDependencyEvidenceHash);
        Assert.Equal(context.Run.Events[0].SequentialNodeEvidence, loaded.Events[0].SequentialNodeEvidence);
        var evidence = Assert.IsType<CustomLoopSequentialNodeEvidence>(loaded.Events[0].SequentialNodeEvidence);
        Assert.Equal(evidence.EvidenceHash, (await restarted.ResolveAsync(evidence.EvidenceHash))?.EvidenceHash);
        var runSource = (IGovernedLoopSequentialRunEvidenceSource)restarted;
        var runEvidence = Assert.IsType<GovernedLoopSequentialRunEvidence>(await runSource.ResolveAsync(context.Run.Id));
        Assert.NotSame(loaded.SequentialAdapterBinding, runEvidence.AdapterBinding);
        Assert.NotSame(loaded.SequentialAdapterBinding!.AdmissionReceipt, runEvidence.AdapterBinding.AdmissionReceipt);
        Assert.NotSame(loaded.SequentialInvocationSnapshot, runEvidence.InvocationSnapshot);
        Assert.NotSame(loaded.SequentialAdapterBinding!.ExecutionBinding, runEvidence.AdapterBinding.ExecutionBinding);
        Assert.NotSame(loaded.SequentialInvocationSnapshot!.ContextManifest, runEvidence.InvocationSnapshot.ContextManifest);
        Assert.Equal(context.Binding.ContentHash, runEvidence.AdapterBinding.ContentHash);
        Assert.Equal(context.Invocation.ContentHash, runEvidence.InvocationSnapshot.ContentHash);
        Assert.True(GovernedLoopSequentialContractValidator.Validate(runEvidence.AdapterBinding).IsValid);
        Assert.True(GovernedLoopSequentialContractValidator.Validate(runEvidence.InvocationSnapshot).IsValid);

        var request = OrderedRequest(context, context.Plan.Nodes[0], loaded.Events[0]);
        var first = await restarted.RetainAsync(request);
        var replay = await restarted.RetainAsync(request);
        var afterReplay = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(context.Run.Id));

        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Completed, first.Status);
        Assert.Equal(evidence.EvidenceHash, first.EvidenceHash);
        Assert.Equal(first, replay);
        Assert.Equal(loaded.LifecycleVersion, afterReplay.LifecycleVersion);
        Assert.Equal(loaded.Events, afterReplay.Events);
    }

    [Fact]
    public async Task Canonical_review_pending_receipt_round_trips_and_replays_without_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        const string Identity = "review-pending-receipt";
        var context = CreateHumanReviewContext(Identity);
        var admitted = await CustomLoopFrontierStoreTests.PersistStrictHumanReviewAdmissionAsync(paths, Identity);
        var parkedEvent = Assert.Single(admitted.Events, item => item.SequentialNodeEvidence?.Disposition == CustomLoopSequentialNodeDisposition.ReviewPending);
        var node = Assert.Single(context.Plan.Nodes, item => string.Equals(item.NodeId, parkedEvent.SequentialNodeEvidence?.NodeId, StringComparison.Ordinal));
        var request = OrderedRequest(context, node, parkedEvent, GovernedLoopSequentialNodeHandlerResultStatus.ReviewPending, admitted.LifecycleVersion);

        using var restarted = new CustomLoopRunStore(paths);
        var first = await restarted.RetainAsync(request);
        var replay = await restarted.RetainAsync(request);
        var afterReplay = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(admitted.Id));

        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.ReviewPending, first.Status);
        Assert.Equal(parkedEvent.SequentialNodeEvidence?.EvidenceHash, first.EvidenceHash);
        Assert.Equal(first, replay);
        Assert.Equal(admitted.LifecycleVersion, afterReplay.LifecycleVersion);
        Assert.Equal(CustomLoopRunArtifactSerializer.Serialize(admitted), CustomLoopRunArtifactSerializer.Serialize(afterReplay));
    }

    [Fact]
    public async Task Canonical_wait_compare_and_swap_is_atomic_restart_safe_and_phase_ordered()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var stages = CreateWaitStages();
        using var first = new CustomLoopRunStore(paths);

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await first.CreateAsync(stages.Admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await first.UpdateAsync(stages.InferenceRunning, stages.Admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await first.UpdateAsync(stages.ReadyForWait, stages.InferenceRunning.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await first.UpdateAsync(stages.WaitRunning, stages.ReadyForWait.LifecycleVersion)).Status);

        using var restarted = new CustomLoopRunStore(paths);
        var parked = await restarted.UpdateAsync(stages.Waiting, stages.WaitRunning.LifecycleVersion);

        Assert.Equal(CustomLoopRunStoreStatus.Updated, parked.Status);
        var retainedWaiting = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(stages.Admitted.Id));
        Assert.Equal(CustomLoopRunStatus.Waiting, retainedWaiting.Status);
        Assert.Equal(stages.Waiting.Frontier!.Payload.ContentHash, retainedWaiting.Frontier!.Payload.ContentHash);
        Assert.Equal(
            CustomLoopRunArtifactSerializer.Serialize(stages.Waiting),
            CustomLoopRunArtifactSerializer.Serialize(retainedWaiting));

        using var staleWriter = new CustomLoopRunStore(paths);
        var stale = await staleWriter.UpdateAsync(stages.Waiting, stages.WaitRunning.LifecycleVersion);
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, stale.Status);
        Assert.Equal(stages.WaitRunning.LifecycleVersion, stale.Conflict?.ExpectedLifecycleVersion);
        Assert.Equal(stages.Waiting.LifecycleVersion, stale.Conflict?.ActualLifecycleVersion);

        Assert.True(CustomLoopRunValidator.Validate(stages.SkippedParkPhase).IsValid);
        await Assert.ThrowsAsync<FormatException>(() => restarted.UpdateAsync(stages.SkippedParkPhase, stages.Waiting.LifecycleVersion));
        Assert.Equal(stages.Waiting.LifecycleVersion, (await restarted.GetAsync(stages.Admitted.Id))?.LifecycleVersion);

        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await restarted.UpdateAsync(stages.Checkpointed, stages.Waiting.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await restarted.UpdateAsync(stages.Continued, stages.Checkpointed.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await restarted.UpdateAsync(stages.Completed, stages.Continued.LifecycleVersion)).Status);

        using var restartedAfterCompletion = new CustomLoopRunStore(paths);
        var completed = Assert.IsType<CustomLoopRunRecord>(await restartedAfterCompletion.GetAsync(stages.Admitted.Id));
        var wait = Assert.Single(completed.WaitEvidence);
        var completion = Assert.Single(completed.Events, item => item.WaitContinuationEvidenceHash is not null);
        Assert.Equal(stages.Completed.Frontier!.Payload.ContentHash, completed.Frontier!.Payload.ContentHash);
        Assert.Equal(
            CustomLoopRunArtifactSerializer.Serialize(stages.Completed),
            CustomLoopRunArtifactSerializer.Serialize(completed));
        Assert.Equal(wait.ContinuationEvidence?.ContentHash, completion.WaitContinuationEvidenceHash);
        Assert.Equal(stages.Completed.Events[^1].EventId, completion.EventId);
        Assert.Equal(stages.Completed.Events[^1].SequentialNodeEvidence?.EvidenceHash, completion.SequentialNodeEvidence?.EvidenceHash);

        var corruptWait = GovernedLoopWaitContractHash.Apply(wait with { ContentHash = string.Empty }) with { ContentHash = Hash('0') };
        var corruptCandidate = completed with
        {
            LifecycleVersion = completed.LifecycleVersion + 1,
            UpdatedAtUtc = completed.UpdatedAtUtc.AddTicks(1),
            WaitEvidence = [corruptWait],
        };
        await Assert.ThrowsAsync<FormatException>(() => restartedAfterCompletion.UpdateAsync(corruptCandidate, completed.LifecycleVersion));
        Assert.Equal(
            CustomLoopRunArtifactSerializer.Serialize(completed),
            CustomLoopRunArtifactSerializer.Serialize(Assert.IsType<CustomLoopRunRecord>(await restartedAfterCompletion.GetAsync(completed.Id))));
    }

    [Fact]
    public async Task Maximum_Human_Input_waiting_checkpoint_round_trips_through_the_real_store_above_the_lifecycle_control_budget()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var stages = CreateMaximumHumanInputWaitingStages();
        using var store = new CustomLoopRunStore(paths);

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(stages.Admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.InferenceRunning, stages.Admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.HumanInputReady, stages.InferenceRunning.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.HumanInputRunning, stages.HumanInputReady.LifecycleVersion)).Status);

        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, stages.Admitted.LoopId, stages.Admitted.Id + ".json");
        var bytesBeforeWaitingCheckpoint = new FileInfo(artifactPath).Length;
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.Waiting, stages.HumanInputRunning.LifecycleVersion)).Status);
        Assert.True(new FileInfo(artifactPath).Length - bytesBeforeWaitingCheckpoint > CustomLoopLimits.MaxTraceControlEventUtf8Bytes);

        using var restarted = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(stages.Admitted.Id));
        var checkpoint = Assert.Single(persisted.HumanInputWaitingCheckpoints);
        Assert.Equal(stages.Configuration.Prompt, checkpoint.NodeConfiguration.Prompt);
        Assert.Equal(stages.Configuration.Prompt, checkpoint.Request.Prompt);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid);
    }

    [Fact]
    public async Task Canonical_wait_reader_rejects_omitted_required_evidence_properties()
    {
        using var sourceWorkspace = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(sourceWorkspace.RootPath);
        var stages = CreateWaitStages();
        using (var source = new CustomLoopRunStore(sourcePaths))
        {
            await PersistWaitStagesAsync(source, stages);
        }

        var sourcePath = Path.Combine(sourcePaths.CustomLoopRunsPath, stages.Admitted.LoopId, stages.Admitted.Id + ".json");
        var canonical = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath))!.AsObject();
        var mutations = new Action<JsonObject>[]
        {
            run => run.Remove("waitEvidence"),
            run => run["events"]!.AsArray()[^1]!.AsObject().Remove("waitContinuationEvidenceHash"),
        };

        foreach (var mutate in mutations)
        {
            using var workspace = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);
            var envelope = canonical.DeepClone().AsObject();
            mutate(envelope["run"]!.AsObject());
            var directory = Path.Combine(paths.CustomLoopRunsPath, stages.Admitted.LoopId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, stages.Admitted.Id + ".json");
            await File.WriteAllBytesAsync(path, [.. Encoding.UTF8.GetBytes(envelope.ToJsonString()), (byte)'\n']);

            await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(paths).GetAsync(stages.Admitted.Id));
        }
    }

    [Fact]
    public async Task Restarted_store_accepts_only_an_exact_trigger_predecessor_when_appending_the_admission_audit_marker()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var paths = new WorkspacePaths(workspace.RootPath);
        using (var writer = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await writer.CreateAsync(context.Run)).Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var loaded = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(context.Run.Id));
        Assert.True(CustomLoopRunValidator.Validate(loaded).IsValid);
        var loadedEvidence = Assert.IsType<CustomLoopSequentialNodeEvidence>(loaded.Events[0].SequentialNodeEvidence);
        Assert.NotSame(context.Run.Events[0].SequentialNodeEvidence!.SelectedControlEdgeIds, loadedEvidence.SelectedControlEdgeIds);
        var sourceSkippedControlEdgeIds = context.Run.Events[0].SequentialNodeEvidence!.SkippedControlEdgeIds;
        Assert.Equal(sourceSkippedControlEdgeIds, loadedEvidence.SkippedControlEdgeIds);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)loadedEvidence.SkippedControlEdgeIds).Add("mutated-edge"));
        Assert.Empty(sourceSkippedControlEdgeIds);
        Assert.Empty(loadedEvidence.SkippedControlEdgeIds);

        var marker = Event(2, "event-admission-audit", CustomLoopRunEventKind.AdmissionAuditCompleted);
        var exact = loaded with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = marker.TimestampUtc,
            Events = [loaded.Events[0] with
            {
                SequentialNodeEvidence = loadedEvidence with
                {
                    SelectedControlEdgeIds = loadedEvidence.SelectedControlEdgeIds.ToArray(),
                    SkippedControlEdgeIds = loadedEvidence.SkippedControlEdgeIds.ToArray(),
                },
            }, marker],
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(loaded, exact).IsValid);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await restarted.UpdateAsync(exact, 1)).Status);
        Assert.True(CustomLoopRunValidator.HasCompleteAdmissionAudit(await restarted.GetAsync(context.Run.Id)));
    }

    [Fact]
    public void Append_only_evidence_comparison_rejects_route_coordinate_and_hash_substitution()
    {
        var context = CreateContext();
        var evidence = Assert.IsType<CustomLoopSequentialNodeEvidence>(context.Run.Events[0].SequentialNodeEvidence);
        var marker = Event(2, "event-admission-audit", CustomLoopRunEventKind.AdmissionAuditCompleted);
        var substitutions = new CustomLoopSequentialNodeEvidence[]
        {
            evidence with { SelectedControlEdgeIds = ["trigger-to-exit"] },
            evidence with { SelectedControlEdgeIds = [], SkippedControlEdgeIds = ["trigger-to-inference"] },
            evidence with { ActivationOrdinal = 1 },
            evidence with { VisitOrdinal = 2 },
            evidence with { Attempt = 2 },
            evidence with { EvidenceHash = Hash('8') },
            evidence with { OutcomeArtifactHash = Hash('8') },
        };

        foreach (var substitution in substitutions)
        {
            var candidate = context.Run with
            {
                LifecycleVersion = 2,
                UpdatedAtUtc = marker.TimestampUtc,
                Events = [context.Run.Events[0] with { SequentialNodeEvidence = substitution }, marker],
            };
            var validation = CustomLoopRunValidator.ValidateUpdate(context.Run, candidate);
            Assert.Contains(validation.Errors, error => error.Code == "event_history_changed" && error.Field == "events[0]");
        }
    }

    [Fact]
    public void Sequential_evidence_value_semantics_are_structural_and_route_order_is_significant()
    {
        var context = CreateContext();
        var trigger = Assert.IsType<CustomLoopSequentialNodeEvidence>(context.Run.Events[0].SequentialNodeEvidence);
        var baseline = CustomLoopSequentialNodeEvidenceHash.Apply(trigger with
        {
            SelectedControlEdgeIds = ["edge-a", "edge-b"],
            SkippedControlEdgeIds = ["edge-c", "edge-d"],
        });
        var independent = baseline with
        {
            SelectedControlEdgeIds = baseline.SelectedControlEdgeIds.ToArray(),
            SkippedControlEdgeIds = baseline.SkippedControlEdgeIds.ToArray(),
        };
        var reordered = CustomLoopSequentialNodeEvidenceHash.Apply(baseline with
        {
            SelectedControlEdgeIds = ["edge-b", "edge-a"],
            EvidenceHash = string.Empty,
        });

        Assert.NotSame(baseline.SelectedControlEdgeIds, independent.SelectedControlEdgeIds);
        Assert.Equal(baseline, independent);
        Assert.Equal(baseline.GetHashCode(), independent.GetHashCode());
        Assert.NotEqual(baseline, reordered);
        Assert.NotEqual(baseline.EvidenceHash, reordered.EvidenceHash);
    }

    [Fact]
    public async Task Canonical_evidence_resolves_after_an_external_process_restart()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var paths = new WorkspacePaths(workspace.RootPath);
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        }

        var resultPath = Path.Combine(workspace.RootPath, "cross-process-evidence-result.txt");
        using var process = StartCrossProcessResolver(
            workspace.RootPath,
            context.Run.Events[0].SequentialNodeEvidence!.EvidenceHash,
            resultPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = await process.StandardError.ReadToEndAsync(timeout.Token);

        Assert.True(process.ExitCode == 0, $"Child exit {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.Equal("resolved", await File.ReadAllTextAsync(resultPath, timeout.Token));
    }

    [Fact]
    public async Task Recorder_rejects_untrusted_ordered_coordinates_without_rewriting_evidence()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var request = OrderedRequest(context, context.Plan.Nodes[0], context.Run.Events[0]);
        var persistedCoordinateSubstitutions = new[]
        {
            request with { OrderedLifecycleVersion = 2 },
            request with { OrderedEventSequence = 2 },
            request with { OrderedEventId = "event-other" },
            request with { Disposition = GovernedLoopSequentialNodeHandlerResultStatus.Rejected },
        };

        foreach (var substitution in persistedCoordinateSubstitutions)
        {
            await Assert.ThrowsAsync<FormatException>(() => store.RetainAsync(substitution));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => store.RetainAsync(request with { Dispatch = request.Dispatch with { Attempt = 2 } }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RetainAsync(request with { Dispatch = request.Dispatch with { Node = context.Plan.Nodes[1] } }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RetainAsync(request with { SchemaVersion = 2 }));
        var loaded = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(context.Run.Id));
        Assert.Equal(context.Run.LifecycleVersion, loaded.LifecycleVersion);
        Assert.Equal(context.Run.Events.Length, loaded.Events.Length);
        Assert.Equal(context.Run.Events[0].EventId, loaded.Events[0].EventId);
        Assert.Equal(context.Run.Events[0].SequentialNodeEvidence, loaded.Events[0].SequentialNodeEvidence);
    }

    [Fact]
    public async Task Recorder_replays_immutable_terminal_evidence_after_a_later_lifecycle_append()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var lifecycleEvent = Event(2, "event-running", CustomLoopRunEventKind.LifecycleChanged);
        var running = context.Run with
        {
            LifecycleVersion = 2,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = _timestamp.AddMinutes(1),
            ExecutionClock = new CustomLoopExecutionClock(0, _timestamp.AddMinutes(1)),
            Events = [.. context.Run.Events, lifecycleEvent],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, 1)).Status);

        var original = OrderedRequest(context, context.Plan.Nodes[0], context.Run.Events[0]);
        var replay = await store.RetainAsync(original);
        Assert.Equal(context.Run.Events[0].SequentialNodeEvidence?.EvidenceHash, replay.EvidenceHash);
        await Assert.ThrowsAsync<FormatException>(() => store.RetainAsync(original with { OrderedLifecycleVersion = 3 }));
        var loaded = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(context.Run.Id));
        Assert.Equal(2, loaded.LifecycleVersion);
        Assert.Equal(lifecycleEvent.EventId, loaded.Events[^1].EventId);
    }

    [Fact]
    public async Task Evidence_sources_fail_closed_for_invalid_missing_and_legacy_coordinates()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var runSource = (IGovernedLoopSequentialRunEvidenceSource)store;

        await Assert.ThrowsAsync<ArgumentException>(() => store.ResolveAsync("not-a-hash"));
        Assert.Null(await store.ResolveAsync(Hash('8')));
        Assert.Null(await runSource.ResolveAsync("run-missing"));
        await Assert.ThrowsAsync<FormatException>(() => store.RetainAsync(OrderedRequest(context, context.Plan.Nodes[0], context.Run.Events[0])));

        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(context.Run.AdmittedDefinition.CapabilityRequirements, _timestamp);
        var legacy = CustomLoopAdmissionRequestHash.Apply(context.Run with
        {
            CapabilityAdmission = capabilityAdmission,
            SequentialInvocationSnapshot = null,
            SequentialAdapterBinding = null,
            Frontier = null,
            Events = [context.Run.Events[0] with { SequentialNodeEvidence = null }],
        });
        Assert.True(CustomLoopRunValidator.Validate(legacy).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(legacy).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(legacy)).Status);
        Assert.Null(await runSource.ResolveAsync(legacy.Id));
    }

    [Fact]
    public void Sequential_converter_backed_coordinates_require_canonical_nested_property_order()
    {
        var context = CreateContext();
        var bindingRoot = JsonNode.Parse(CustomLoopRunArtifactSerializer.Serialize(context.Run))!.AsObject();
        var binding = bindingRoot["run"]!["sequentialAdapterBinding"]!["executionBinding"]!.AsObject();
        bindingRoot["run"]!["sequentialAdapterBinding"]!["executionBinding"] = ReverseProperties(binding);
        AssertCanonicalOrderRejected(bindingRoot);

        var evidenceRoot = JsonNode.Parse(CustomLoopRunArtifactSerializer.Serialize(context.Run))!.AsObject();
        var revision = evidenceRoot["run"]!["events"]![0]!["sequentialNodeEvidence"]!["revision"]!.AsObject();
        evidenceRoot["run"]!["events"]![0]!["sequentialNodeEvidence"]!["revision"] = ReverseProperties(revision);
        AssertCanonicalOrderRejected(evidenceRoot);
    }

    [Fact]
    public void Pure_node_outcome_round_trips_through_the_canonical_content_registry()
    {
        var context = CreateContext();
        var pure = WithPureFrontier(context.Run);
        var start = PureEvent(2, "event-pure-start", CustomLoopRunEventKind.NodeAttemptStarted, context.Binding);
        var outcomeJson = "{\"schemaVersion\":1,\"nodeId\":\"step-1\",\"value\":\"retained\"}";
        var completion = PureEvent(3, "event-pure-complete", CustomLoopRunEventKind.NodeAttemptCompleted, context.Binding, outcomeJson);
        var run = pure with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(2),
            Frontier = CompletePureFrontier(StartPureFrontier(pure.Frontier!, start), start, completion),
            Events = [pure.Events[0], start, completion],
        };

        var encoded = CustomLoopRunArtifactSerializer.Serialize(run);
        var root = JsonNode.Parse(encoded)!.AsObject();
        var reference = root["run"]!["events"]![2]!["pureNodeOutcomeJson"]!.AsObject()["$content"]!.GetValue<string>();
        var entry = root["content"]!.AsArray().Select(item => item!.AsObject()).Single(item => item["id"]!.GetValue<string>() == reference);
        var retained = Encoding.UTF8.GetString(Convert.FromBase64String(entry["base64"]!.GetValue<string>()));
        var decoded = CustomLoopRunArtifactSerializer.Deserialize(encoded);

        Assert.Equal(outcomeJson, retained);
        Assert.Equal(outcomeJson, decoded.Events[2].PureNodeOutcomeJson);
        Assert.True(CustomLoopSequentialOutcomeArtifactHash.Matches(decoded.Events[2]));
        Assert.Equal(encoded, CustomLoopRunArtifactSerializer.Serialize(decoded));

        var missingRequiredProperty = JsonNode.Parse(encoded)!.AsObject();
        Assert.True(missingRequiredProperty["run"]!["events"]![2]!.AsObject().Remove("pureNodeOutcomeJson"));
        Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(missingRequiredProperty.ToJsonString() + "\n")));
    }

    [Fact]
    public async Task Pure_node_completion_uses_its_base64_aware_reservation_without_widening_provider_attempts()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var pure = WithPureFrontier(context.Run);
        var paths = new WorkspacePaths(workspace.RootPath);
        var start = PureEvent(2, "event-pure-start", CustomLoopRunEventKind.NodeAttemptStarted, context.Binding);
        Assert.Equal(CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes, start.TraceReservationUtf8Bytes);
        var started = pure with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = _timestamp.AddMinutes(1),
            Frontier = StartPureFrontier(pure.Frontier!, start),
            Events = [.. pure.Events, start],
        };
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(pure)).Status);
            Assert.True(await store.HasSufficientTraceCapacityForDispatchAsync(started, 1));
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(started, 1)).Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(pure.Id));
        Assert.Equal(CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes, recovered.Events[^1].TraceReservationUtf8Bytes);
        Assert.True(await restarted.HasSufficientTraceCapacityForDispatchAsync(recovered, 2));

        var outcomeJson = "{\"payload\":\"" + new string('x', CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes * 2) + "\"}";
        var completion = PureEvent(3, "event-pure-complete", CustomLoopRunEventKind.NodeAttemptCompleted, context.Binding, outcomeJson);
        var completed = recovered with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(2),
            Frontier = CompletePureFrontier(recovered.Frontier!, start, completion),
            Events = [.. recovered.Events, completion],
        };
        var beforeBytes = CustomLoopRunArtifactSerializer.Serialize(recovered).LongLength;
        var afterBytes = CustomLoopRunArtifactSerializer.Serialize(completed).LongLength;
        Assert.True(afterBytes - beforeBytes > CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        Assert.True(afterBytes - beforeBytes < CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await restarted.UpdateAsync(completed, 2)).Status);
    }

    [Fact]
    public async Task Store_accepts_multiple_unfinished_pure_nodes_without_reserving_every_maximum_outcome()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var pending = WithPendingPureFrontier(context.Run, 3);

        Assert.True(CustomLoopRunValidator.Validate(pending).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(pending).Errors));
        using var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(pending)).Status);
        var retained = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(pending.Id));
        Assert.Equal(3, retained.Frontier!.Payload.Nodes.Count(node => node.Descriptor.Kind == GovernedLoopNodeKind.Transform));
    }

    [Fact]
    public async Task Store_rejects_missing_or_payload_substituted_trigger_outcome()
    {
        using var missingWorkspace = new TestWorkspace();
        var context = CreateContext();
        var missing = CustomLoopAdmissionRequestHash.Apply(context.Run with
        {
            Events = [context.Run.Events[0] with { SequentialNodeEvidence = null }],
        });
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(new WorkspacePaths(missingWorkspace.RootPath)).CreateAsync(missing));

        using var substitutedWorkspace = new TestWorkspace();
        var substituted = CustomLoopAdmissionRequestHash.Apply(context.Run with
        {
            Events = [context.Run.Events[0] with { Detail = "Substituted after its exact outcome digest was computed." }],
        });
        await Assert.ThrowsAsync<FormatException>(() => new CustomLoopRunStore(new WorkspacePaths(substitutedWorkspace.RootPath)).CreateAsync(substituted));
    }

    [Fact]
    public async Task Dispatch_start_is_not_terminal_evidence_and_checkpoint_cannot_advance_without_an_outcome()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var start = WithEvidence(
            Event(2, "event-inference-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var selection = GovernedLoopSequentialFrontierMachine.Select(context.Run.Frontier, context.Binding, context.Plan);
        var startedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            context.Run.Frontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[1],
            selection.Activation,
            1,
            start.EventId,
            start.TimestampUtc).Frontier);
        var started = context.Run with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = _timestamp.AddMinutes(1),
            Frontier = startedFrontier,
            Events = [.. context.Run.Events, start],
        };

        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(started, 1)).Status);
        Assert.Null(await store.ResolveAsync(start.SequentialNodeEvidence!.EvidenceHash));

        var checkpointWithoutOutcome = started with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(2),
            Checkpoint = started.Checkpoint with { NextStepIndex = 1, LastCommittedSequence = 2 },
        };
        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(checkpointWithoutOutcome, 2));
    }

    [Fact]
    public async Task Store_rejects_duplicate_terminal_evidence_for_one_node_attempt()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var start = WithEvidence(
            Event(2, "event-inference-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var selection = GovernedLoopSequentialFrontierMachine.Select(context.Run.Frontier, context.Binding, context.Plan);
        var startedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            context.Run.Frontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[1],
            selection.Activation,
            1,
            start.EventId,
            start.TimestampUtc).Frontier);
        var started = context.Run with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = _timestamp.AddMinutes(1),
            Frontier = startedFrontier,
            Events = [.. context.Run.Events, start],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(started, 1)).Status);
        var first = WithEvidence(
            Event(3, "event-inference-completed", CustomLoopRunEventKind.NodeAttemptCompleted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var duplicate = WithEvidence(
            Event(4, "event-inference-completed-again", CustomLoopRunEventKind.NodeAttemptCompleted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var invalid = started with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(3),
            Events = [.. started.Events, first, duplicate],
        };

        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(invalid, 2));
        var loaded = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(context.Run.Id));
        Assert.Equal(2, loaded.LifecycleVersion);
        Assert.Equal(2, loaded.Events.Length);
    }

    [Fact]
    public async Task Trace_capacity_accepts_exactly_one_deterministic_canonical_exit_after_the_model_attempt_ceiling()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var afterInference = await CompleteCanonicalInferenceAsync(store, context);
        var exitStarted = WithEvidence(
            Event(4, "event-exit-started", CustomLoopRunEventKind.ExitDecisionStarted, "exit", 1),
            context.Binding,
            "exit",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var exitReady = Assert.IsType<GovernedLoopNodeExecutionEvidence>(
            GovernedLoopSequentialFrontierMachine.Select(afterInference.Frontier, context.Binding, context.Plan).Activation);
        var exitRunning = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            afterInference.Frontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[2],
            exitReady,
            1,
            exitStarted.EventId,
            exitStarted.TimestampUtc).Frontier);
        var startedExitRun = afterInference with
        {
            LifecycleVersion = 4,
            UpdatedAtUtc = exitStarted.TimestampUtc,
            Frontier = exitRunning,
            Events = [.. afterInference.Events, exitStarted],
        };

        Assert.True(await store.HasSufficientTraceCapacityForDispatchAsync(startedExitRun, 3));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(startedExitRun, 3)).Status);
    }

    [Fact]
    public async Task Trace_capacity_admits_one_canonical_scheduled_retry_wake_as_one_atomic_store_update()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var stages = await CreateCheckpointedRetryStagesAsync(paths, "retry-capacity");
        var store = stages.Store;
        var context = stages.Context;
        var activation = stages.Activation;
        var checkpointed = stages.Checkpointed;
        var attached = stages.Attached;
        var eligibleAtUtc = stages.EligibleAtUtc;
        var operationId = attached.AttemptOperationId!;
        var budget = attached.Budget;

        var due = RetrySuccessor(attached, GovernedLoopRetryStateDisposition.Due, budget, eligibleAtUtc);
        var reserved = RetrySuccessor(due, GovernedLoopRetryStateDisposition.Reserved, new GovernedLoopRetryBudgetSnapshot(2, null, null, null, null, 2), eligibleAtUtc);
        var dispatched = RetrySuccessor(reserved, GovernedLoopRetryStateDisposition.Dispatched, reserved.Budget, eligibleAtUtc);
        var resumedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.ResumeRetry(
            checkpointed.Frontier,
            context.Binding,
            context.Plan,
            checkpointed.Frontier!.Payload.Nodes[activation.ActivationOrdinal],
            2,
            operationId,
            eligibleAtUtc).Frontier);
        var resumed = checkpointed with
        {
            LifecycleVersion = 6,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = eligibleAtUtc,
            ExecutionClock = checkpointed.ExecutionClock with { ActiveSinceUtc = eligibleAtUtc },
            Frontier = resumedFrontier,
            Events = [
                .. checkpointed.Events,
                RetryStateEvent(9, due) with { Detail = MaximumRetryStateDetail() },
                RetryStateEvent(10, reserved) with { Detail = MaximumRetryStateDetail() },
                RetryStateEvent(11, dispatched) with { Detail = MaximumRetryStateDetail() },
                RetryLifecycleEvent(12, eligibleAtUtc, "Ordered execution resumed for one exact bounded retry."),
            ],
        };

        using var maximumWorkspace = new TestWorkspace();
        var maximumStages = await CreateCheckpointedRetryStagesAsync(
            new WorkspacePaths(maximumWorkspace.RootPath),
            "retry-capacity-maximum-shape",
            MaximumRetryStateDetail());
        var maximumAttachmentDelta = CustomLoopRunArtifactSerializer.Serialize(maximumStages.Checkpointed).Length - CustomLoopRunArtifactSerializer.Serialize(maximumStages.ScheduledRun).Length;
        Assert.InRange(maximumAttachmentDelta, 1, CustomLoopLimits.MaxRetryStateEventUtf8Bytes);
        Assert.True(maximumStages.Checkpointed.Events[^1].RetryState is
        {
            Disposition: GovernedLoopRetryStateDisposition.Scheduled,
            NextAttempt: 2,
            AttemptOperationId: not null,
            WakeCheckpointId: not null,
            WakeCheckpointHash: not null,
            FailureEvidenceId: not null,
            FailureEvidenceHash: not null,
        });
        var resumedDelta = CustomLoopRunArtifactSerializer.Serialize(resumed).Length - CustomLoopRunArtifactSerializer.Serialize(checkpointed).Length;
        Assert.InRange(
            resumedDelta,
            1,
            checked((3 * CustomLoopLimits.MaxRetryStateEventUtf8Bytes) + CustomLoopLimits.MaxTraceControlEventUtf8Bytes));

        var oversized = resumed with
        {
            Events = [
                .. checkpointed.Events,
                RetryStateEvent(9, due) with { Detail = new string('x', CustomLoopLimits.MaxRetryStateDetailCharacters + 1) },
                RetryStateEvent(10, reserved),
                RetryStateEvent(11, dispatched),
                RetryLifecycleEvent(12, eligibleAtUtc, "Ordered execution resumed for one exact bounded retry."),
            ],
        };
        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(oversized, 5));
        var malformed = resumed with
        {
            Events = [
                .. checkpointed.Events,
                RetryStateEvent(9, due with { ContentHash = new string('0', 64) }),
                RetryStateEvent(10, reserved),
                RetryStateEvent(11, dispatched),
                RetryLifecycleEvent(12, eligibleAtUtc, "Ordered execution resumed for one exact bounded retry."),
            ],
        };
        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(malformed, 5));
        var prematureStart = WithEvidence(
            Event(13, operationId, CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 2) with { TimestampUtc = eligibleAtUtc },
            context.Binding,
            "step-1",
            2,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var unrelated = resumed with { Events = [.. resumed.Events, prematureStart] };
        await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(unrelated, 5));

        Assert.True(await store.HasSufficientTraceCapacityForDispatchAsync(resumed, 5));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(resumed, 5)).Status);
        var stored = Assert.IsType<CustomLoopRunRecord>(await new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath)).GetAsync(context.Run.Id));
        Assert.Equal(CustomLoopRunStatus.Running, stored.Status);
        Assert.Equal(
            [GovernedLoopRetryStateDisposition.FailureRetained, GovernedLoopRetryStateDisposition.Scheduled, GovernedLoopRetryStateDisposition.Scheduled, GovernedLoopRetryStateDisposition.Due, GovernedLoopRetryStateDisposition.Reserved, GovernedLoopRetryStateDisposition.Dispatched],
            stored.Events.Where(item => item.RetryState is not null).Select(item => item.RetryState!.Disposition));

        var retryStart = WithEvidence(
            Event(13, operationId, CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 2) with { TimestampUtc = eligibleAtUtc },
            context.Binding,
            "step-1",
            2,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var retried = resumed with
        {
            LifecycleVersion = 7,
            UpdatedAtUtc = retryStart.TimestampUtc,
            Events = [.. resumed.Events, retryStart],
        };

        Assert.True(await store.HasSufficientTraceCapacityForDispatchAsync(retried, 6));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(retried, 6)).Status);

        var extraStart = WithEvidence(
            Event(14, "retry-unadmitted-attempt", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 3) with { TimestampUtc = retryStart.TimestampUtc },
            context.Binding,
            "step-1",
            3,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var unadmitted = retried with
        {
            LifecycleVersion = 8,
            UpdatedAtUtc = extraStart.TimestampUtc,
            Events = [.. retried.Events, extraStart],
        };

        var unadmittedException = await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(unadmitted, 7));
        Assert.Contains("attempts after one require an earlier exact durable retry-dispatch reservation", unadmittedException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trace_capacity_admits_a_terminal_retry_exhaustion_with_exact_failure_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var stages = await CreateCheckpointedRetryStagesAsync(paths, "retry-exhaustion-capacity");
        var checkpointed = stages.Checkpointed;
        var attached = stages.Attached;
        var eligibleAtUtc = stages.EligibleAtUtc;
        var waitingActivation = checkpointed.Frontier!.Payload.Nodes[stages.Activation.ActivationOrdinal];
        var due = RetrySuccessor(attached, GovernedLoopRetryStateDisposition.Due, attached.Budget, eligibleAtUtc);
        var exhausted = RetryTerminalSuccessor(due, GovernedLoopRetryStateDisposition.Exhausted, due.Budget, eligibleAtUtc);
        var dueEvent = RetryStateEvent(9, due);
        var exhaustedEvent = RetryStateEvent(10, exhausted);
        var exhaustionEvent = RetryExhaustionEvent(
            checkpointed with { Events = [.. checkpointed.Events, dueEvent, exhaustedEvent] },
            stages.Context.Plan,
            waitingActivation,
            exhaustedEvent,
            exhausted,
            eligibleAtUtc,
            "retry-budget-exhausted");
        var failedTransition = GovernedLoopSequentialFrontierMachine.FailWaiting(
            checkpointed.Frontier,
            stages.Context.Binding,
            stages.Context.Plan,
            stages.Context.Plan.Nodes[waitingActivation.PlanOrdinal],
            waitingActivation,
            waitingActivation.Attempt!.Value,
            attached.AttemptOperationId,
            exhaustionEvent.EventId,
            exhaustionEvent.SequentialNodeEvidence!.OutcomeArtifactHash,
            GovernedLoopControlCondition.Failure,
            eligibleAtUtc,
            [],
            null);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, failedTransition.Status);
        var terminalFrontier = Assert.IsType<GovernedLoopFrontierPosture>(failedTransition.Frontier);
        Assert.Equal(GovernedLoopFrontierStatus.Failed, terminalFrontier.Payload.Status);
        var terminal = checkpointed with
        {
            LifecycleVersion = 6,
            Status = CustomLoopRunStatus.Failed,
            UpdatedAtUtc = eligibleAtUtc,
            CompletedAtUtc = eligibleAtUtc,
            FailureCode = "canonical_retry_budget_exhausted",
            FailureDetail = "retry-budget-exhausted",
            FinalOutput = null,
            Frontier = terminalFrontier,
            Events = [
                .. checkpointed.Events,
                dueEvent,
                exhaustedEvent,
                exhaustionEvent,
                RetryLifecycleEvent(12, eligibleAtUtc, "The retry budget was exhausted without dispatch and the run stopped with exact classified evidence."),
            ],
        };

        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await stages.Store.UpdateAsync(terminal, 5)).Status);
        var stored = Assert.IsType<CustomLoopRunRecord>(await new CustomLoopRunStore(paths).GetAsync(stages.Context.Run.Id));
        Assert.Equal(CustomLoopRunStatus.Failed, stored.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.Exhausted, stored.Events[^3].RetryState?.Disposition);
        Assert.Equal(
            [CustomLoopRunEventKind.RetryStateChanged, CustomLoopRunEventKind.RetryStateChanged, CustomLoopRunEventKind.NodeAttemptFailed, CustomLoopRunEventKind.LifecycleChanged],
            stored.Events.TakeLast(4).Select(item => item.Kind));
    }

    [Fact]
    public async Task Trace_capacity_rejects_a_near_limit_scheduled_retry_when_its_full_successor_chain_cannot_fit()
    {
        using var workspace = new TestWorkspace();
        var padding = NearLimitRetryCapacityContextBlocks();

        // The padded trace itself remains below the hard artifact limit. The public Scheduled
        // update reaches the store, which must retain capacity for its checkpoint, wake,
        // dispatch, and later retry-safe completion successors instead of accepting a suffix
        // that cannot finish canonically.
        var stages = await CreateCheckpointedRetryStagesAsync(
            new WorkspacePaths(workspace.RootPath),
            "retry-capacity-near-limit",
            admissionContextBlocks: padding,
            permitCapacityRefusal: true);

        Assert.Equal(CustomLoopRunStoreStatus.LimitExceeded, stages.LatestStoreStatus);
        var stored = Assert.IsType<CustomLoopRunRecord>(await new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath)).GetAsync(stages.Context.Run.Id));
        Assert.Equal(3, stored.LifecycleVersion);
    }

    [Fact]
    public async Task Trace_capacity_does_not_widen_the_legacy_model_attempt_ceiling()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var legacy = AsLegacyRun(context.Run);
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(legacy)).Status);
        var started = legacy with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = _timestamp.AddMinutes(1),
            Events = [.. legacy.Events, Event(2, "event-legacy-started", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1)],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(started, 1)).Status);
        var completed = started with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(2),
            Events = [.. started.Events, Event(3, "event-legacy-completed", CustomLoopRunEventKind.NodeAttemptCompleted, "step-1", 1)],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, 2)).Status);
        var overLimit = completed with
        {
            LifecycleVersion = 4,
            UpdatedAtUtc = _timestamp.AddMinutes(3),
            Events = [.. completed.Events, Event(4, "event-legacy-second-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 2)],
        };

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(overLimit, 3));
        Assert.Contains("more provider-attempt starts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trace_capacity_rejects_a_second_canonical_non_exit_model_attempt()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var afterInference = await CompleteCanonicalInferenceAsync(store, context);
        var secondStart = WithEvidence(
            Event(4, "event-inference-second-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 2),
            context.Binding,
            "step-1",
            2,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var overLimit = afterInference with
        {
            LifecycleVersion = 4,
            UpdatedAtUtc = _timestamp.AddMinutes(3),
            Events = [.. afterInference.Events, secondStart],
        };

        var exception = await Assert.ThrowsAsync<FormatException>(() => store.UpdateAsync(overLimit, 3));
        Assert.Contains("exact durable frontier activation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trace_capacity_admits_a_fail_terminal_without_consuming_another_provider_attempt()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext(FailGraph());
        var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);

        var inferenceSelection = GovernedLoopSequentialFrontierMachine.Select(context.Run.Frontier, context.Binding, context.Plan);
        var inferenceActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(inferenceSelection.Activation);
        var inferenceStart = WithEvidence(
            Event(2, "event-inference-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var inferenceRunningFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            context.Run.Frontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[1],
            inferenceActivation,
            1,
            inferenceStart.EventId,
            inferenceStart.TimestampUtc).Frontier);
        var inferenceRunning = context.Run with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = inferenceStart.TimestampUtc,
            Frontier = inferenceRunningFrontier,
            Events = [.. context.Run.Events, inferenceStart],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(inferenceRunning, 1)).Status);

        var inferenceFailure = WithEvidence(
            Event(3, "event-inference-failed", CustomLoopRunEventKind.NodeAttemptFailed, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            CustomLoopSequentialNodeDisposition.Rejected,
            new GovernedLoopFailureEvidenceReference(inferenceStart.EventId, inferenceStart.SequentialNodeEvidence!.EvidenceHash),
            selectedControlEdgeIdsOverride: ["step-to-fail"],
            skippedControlEdgeIdsOverride: ["step-to-exit"]);
        var failReadyFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.FailRunning(
            inferenceRunningFrontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[1],
            inferenceRunningFrontier.Payload.Nodes[inferenceActivation.ActivationOrdinal],
            1,
            inferenceStart.EventId,
            inferenceFailure.EventId,
            inferenceFailure.SequentialNodeEvidence!.OutcomeArtifactHash,
            GovernedLoopControlCondition.Failure,
            inferenceFailure.TimestampUtc).Frontier);
        var inferenceFailed = inferenceRunning with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = inferenceFailure.TimestampUtc,
            Frontier = failReadyFrontier,
            Events = [.. inferenceRunning.Events, inferenceFailure],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(inferenceFailed, 2)).Status);

        var failSelection = GovernedLoopSequentialFrontierMachine.Select(failReadyFrontier, context.Binding, context.Plan);
        var failActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(failSelection.Activation);
        var failStart = WithEvidence(
            Event(4, "event-fail-start", CustomLoopRunEventKind.NodeAttemptStarted, "fail", 1) with
            {
                TraceReservationUtf8Bytes = CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
            },
            context.Binding,
            "fail",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            activationOrdinalOverride: failActivation.ActivationOrdinal);
        var failRunningFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            failReadyFrontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[failActivation.PlanOrdinal],
            failActivation,
            1,
            failStart.EventId,
            failStart.TimestampUtc).Frontier);
        var failRunning = inferenceFailed with
        {
            LifecycleVersion = 4,
            UpdatedAtUtc = failStart.TimestampUtc,
            Frontier = failRunningFrontier,
            Events = [.. inferenceFailed.Events, failStart],
        };

        var result = await store.UpdateAsync(failRunning, 3);

        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        Assert.Equal(failStart.EventId, (await store.GetAsync(context.Run.Id))?.Events[^1].EventId);
    }

    [Fact]
    public async Task Persisted_marker_digest_substitution_fails_closed_after_restart()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var paths = new WorkspacePaths(workspace.RootPath);
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        }

        var evidence = context.Run.Events[0].SequentialNodeEvidence!;
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, context.Run.LoopId, context.Run.Id + ".json");
        var persisted = await File.ReadAllTextAsync(artifactPath);
        var substituted = persisted.Replace(evidence.EvidenceHash, Hash('9'), StringComparison.Ordinal);
        Assert.NotEqual(persisted, substituted);
        await File.WriteAllTextAsync(artifactPath, substituted);

        using var restarted = new CustomLoopRunStore(paths);
        await Assert.ThrowsAsync<FormatException>(() => restarted.GetAsync(context.Run.Id));
        await Assert.ThrowsAsync<FormatException>(() => restarted.ResolveAsync(evidence.EvidenceHash));
    }

    [Fact]
    public async Task Concurrent_terminal_outcomes_have_one_cas_winner_and_one_resolvable_receipt()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var firstStore = new CustomLoopRunStore(paths);
        using var secondStore = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await firstStore.CreateAsync(context.Run)).Status);
        var start = WithEvidence(
            Event(2, "event-inference-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var selection = GovernedLoopSequentialFrontierMachine.Select(context.Run.Frontier, context.Binding, context.Plan);
        var startedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            context.Run.Frontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[1],
            selection.Activation,
            1,
            start.EventId,
            start.TimestampUtc).Frontier);
        var started = context.Run with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = _timestamp.AddMinutes(1),
            Frontier = startedFrontier,
            Events = [.. context.Run.Events, start],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await firstStore.UpdateAsync(started, 1)).Status);

        var completedEvent = WithEvidence(
            Event(3, "event-inference-completed", CustomLoopRunEventKind.NodeAttemptCompleted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var rejectedEvent = WithEvidence(
            Event(3, "event-inference-rejected", CustomLoopRunEventKind.NodeAttemptFailed, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            CustomLoopSequentialNodeDisposition.Rejected,
            new GovernedLoopFailureEvidenceReference(start.EventId, start.SequentialNodeEvidence!.EvidenceHash));
        var completed = started with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(2),
            Events = [.. started.Events, completedEvent],
        };
        var rejected = started with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(2),
            Events = [.. started.Events, rejectedEvent],
        };

        var results = await Task.WhenAll(firstStore.UpdateAsync(completed, 2), secondStore.UpdateAsync(rejected, 2));
        Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Updated);
        Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Conflict);
        var winner = Assert.IsType<CustomLoopRunRecord>((await firstStore.GetAsync(context.Run.Id)));
        var terminal = Assert.IsType<CustomLoopSequentialNodeEvidence>(winner.Events[^1].SequentialNodeEvidence);
        Assert.Equal(terminal.EvidenceHash, (await secondStore.ResolveAsync(terminal.EvidenceHash))?.EvidenceHash);
        var loserHash = winner.Events[^1].EventId == completedEvent.EventId
            ? rejectedEvent.SequentialNodeEvidence!.EvidenceHash
            : completedEvent.SequentialNodeEvidence!.EvidenceHash;
        Assert.Null(await secondStore.ResolveAsync(loserHash));
    }

    [Fact]
    public async Task Classified_failure_round_trips_with_exact_precedence_and_tampering_fails_closed_after_restart()
    {
        using var workspace = new TestWorkspace();
        var context = CreateContext();
        var paths = new WorkspacePaths(workspace.RootPath);
        GovernedLoopFailureEvidence? expectedFailure = null;
        using (var store = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
            var start = WithEvidence(
                Event(2, "event-failure-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
                context.Binding,
                "step-1",
                1,
                CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                CustomLoopSequentialNodeDisposition.Unknown);
            var selection = GovernedLoopSequentialFrontierMachine.Select(context.Run.Frontier, context.Binding, context.Plan);
            var startedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
                context.Run.Frontier,
                context.Binding,
                context.Plan,
                context.Plan.Nodes[1],
                selection.Activation,
                1,
                start.EventId,
                start.TimestampUtc).Frontier);
            var started = context.Run with
            {
                LifecycleVersion = 2,
                UpdatedAtUtc = _timestamp.AddMinutes(1),
                Frontier = startedFrontier,
                Events = [.. context.Run.Events, start],
            };
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(started, 1)).Status);

            var rejectedEvent = WithEvidence(
                Event(3, "event-failure-rejected", CustomLoopRunEventKind.NodeAttemptFailed, "step-1", 1),
                context.Binding,
                "step-1",
                1,
                CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                CustomLoopSequentialNodeDisposition.Rejected,
                new GovernedLoopFailureEvidenceReference(start.EventId, start.SequentialNodeEvidence!.EvidenceHash));
            var rejected = started with
            {
                LifecycleVersion = 3,
                UpdatedAtUtc = _timestamp.AddMinutes(2),
                Events = [.. started.Events, rejectedEvent],
            };
            expectedFailure = rejectedEvent.FailureEvidence;
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(rejected, 2)).Status);
        }

        using (var restarted = new CustomLoopRunStore(paths))
        {
            var loaded = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(context.Run.Id));
            var failure = Assert.IsType<GovernedLoopFailureEvidence>(loaded.Events[^1].FailureEvidence);
            Assert.NotNull(expectedFailure);
            Assert.Equal(expectedFailure.ContentHash, failure.ContentHash);
            Assert.Equal(expectedFailure.CausalEvidence, failure.CausalEvidence);
            Assert.Equal(700, failure.Precedence);
            Assert.Equal("persistence-fixture-rejected", failure.ServerCode);
            Assert.Equal(failure.ContentHash, loaded.Events[^1].SequentialNodeEvidence?.FailureEvidenceHash);
            Assert.NotSame(expectedFailure, failure);
        }

        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, context.Run.LoopId, context.Run.Id + ".json");
        var persisted = await File.ReadAllTextAsync(artifactPath);
        var tampered = persisted.Replace("\"precedence\":700", "\"precedence\":699", StringComparison.Ordinal);
        Assert.NotEqual(persisted, tampered);
        await File.WriteAllTextAsync(artifactPath, tampered);

        using var corruptRestart = new CustomLoopRunStore(paths);
        await Assert.ThrowsAsync<FormatException>(() => corruptRestart.GetAsync(context.Run.Id));
    }

    internal static SequentialContext CreateContext(GovernedLoopSequentialTriggerOrigin? triggerOrigin = null, string identity = "sequential", bool scheduleTrigger = false)
        => CreateContext(LinearGraph(scheduleTrigger), triggerOrigin, identity, scheduleTrigger);

    internal static SequentialContext CreateHumanReviewContext(string identity)
        => CreateContext(HumanReviewGraph(), identity: identity);

    internal static SequentialContext CreatePreDispatchEffectContext(string identity, string? workspaceId = null)
        => CreateContext(HumanReviewOrderedReleaseGraphFixture.PreDispatchEffectGraph(), identity: identity, preDispatchEffect: true, workspaceId: workspaceId);

    private static SequentialContext CreateContext(
        GovernedLoopGraphDefinition graph,
        GovernedLoopSequentialTriggerOrigin? triggerOrigin = null,
        string identity = "sequential",
        bool scheduleTrigger = false,
        CustomLoopContextBlock[]? admissionContextBlocks = null,
        bool preDispatchEffect = false,
        string? workspaceId = null)
    {
        var revisionArtifact = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            null,
            null,
            "create-sequential",
            "user-owner",
            _timestamp);
        var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(1, revisionArtifact, graph);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, graph.RevisionReference, "publish-sequential", Hash('7'));
        var invocationContext = CustomLoopContextSnapshot.CreateEmpty(_timestamp);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            "Execute the exact admitted request.",
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            _timestamp,
            invocationContext.SourceManifest,
            string.Empty)
        {
            TriggerOrigin = triggerOrigin,
        });
        var grant = AuthorityGrantTestFixture.Grant();
        var grantReference = new EmbodySense.Core.Common.Authority.Grants.Models.AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        var request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            1,
            $"admit-{identity}",
            invocation.ContentHash,
            string.Empty,
            publication,
            grantReference,
            AuthorityGrantTestFixture.Actor("user-owner"),
            "web"));
        var effectiveWorkspaceId = workspaceId ?? GovernedLoopAdmissionTestFixture.WorkspaceId;
        var intent = new GovernedLoopAdmissionIntent(
            1,
            effectiveWorkspaceId,
            request.OperationId,
            request.RequestHash,
            publication,
            grantReference,
            graph.OwningRole,
            request.ActorId,
            request.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var execution = GovernedLoopExecutionBinding.Create(1, $"run-{identity}", graph.RevisionReference, 1);
        var capabilityAdmission = preDispatchEffect
            ? PreDispatchEffectCapabilityAdmission(artifact.ArtifactHash, effectiveWorkspaceId)
            : SequentialCapabilityAdmission(artifact.ArtifactHash, scheduleTrigger);
        var effectiveAuthority = preDispatchEffect
            ? new EmbodySense.Core.Common.Authority.Models.AuthorityCeiling(
                capabilityAdmission.Pins.Select(pin => pin.DescriptorIdentity).ToArray(),
                [],
                1,
                CapabilitySideEffectClass.LocalReversible,
                false,
                false,
                false)
            : GovernedLoopAdmissionTestFixture.EffectiveAuthority();
        var grantBoundary = new AuthorityGrantBoundary(_timestamp.AddMinutes(-1), _timestamp.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var grantDependencyEvidenceHash = Hash('9');
        var evaluatedAtUtc = _timestamp.AddMinutes(1);
        var modelRoutingAdmission = preDispatchEffect
            ? GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
                intent,
                execution,
                grant.Binding.Profile,
                grantBoundary,
                grantDependencyEvidenceHash,
                effectiveAuthority,
                capabilityAdmission,
                evaluatedAtUtc)
            : ModelRoutingAdmission(
                graph,
                intent,
                execution,
                grant.Binding.Profile,
                grantBoundary,
                grantDependencyEvidenceHash,
                effectiveAuthority,
                capabilityAdmission,
                evaluatedAtUtc);
        var admissionEvidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            1,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            grant.Binding.Profile,
            grantBoundary,
            grantDependencyEvidenceHash,
            effectiveAuthority,
            capabilityAdmission,
            modelRoutingAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilityAdmission, modelRoutingAdmission),
            evaluatedAtUtc,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            1,
            intent,
            admissionEvidence,
            _timestamp.AddMinutes(2),
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        var binding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            1,
            intent.WorkspaceId,
            execution,
            request.OperationId,
            receipt,
            receipt.ContentHash,
            request.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        Assert.True(
            planResult.Status == GovernedLoopSequentialPlanBuildStatus.Ready,
            $"The canonical persistence graph was rejected with `{planResult.Status}` at `{planResult.FailurePath}`.");
        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(binding, request, receipt, invocation, artifact);
        Assert.True(
            anchorResult.Status == GovernedLoopSequentialRunAnchorStatus.Ready,
            $"The canonical persistence fixture was rejected with `{anchorResult.Status}`.");
        var anchor = Assert.IsType<GovernedLoopSequentialRunAnchor>(anchorResult.Anchor);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(planResult.Plan);

        var definition = preDispatchEffect
            ? Assert.IsType<CustomLoopDefinition>(GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, plan, artifact).Definition)
            : CustomLoopDefinition.CreateSeed("sequential-loop", "default-role", "step-1", "create-loop", _timestamp);
        var admitted = WithEvidence(
            Event(1, "event-admitted", CustomLoopRunEventKind.Admitted),
            binding,
            "trigger",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            outgoingControlEdgeIdsOverride: preDispatchEffect ? plan.Nodes[0].OutgoingControlEdgeIds.ToArray() : null,
            selectedControlEdgeIdsOverride: preDispatchEffect ? plan.Nodes[0].OutgoingControlEdgeIds.ToArray() : null);
        if (admissionContextBlocks is not null)
        {
            var padded = admitted with { ContextBlocks = admissionContextBlocks };
            var evidence = Assert.IsType<CustomLoopSequentialNodeEvidence>(padded.SequentialNodeEvidence) with
            {
                OutcomeArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(padded),
            };
            admitted = padded with { SequentialNodeEvidence = CustomLoopSequentialNodeEvidenceHash.Apply(evidence) };
        }
        var run = new CustomLoopRunRecord(
            1,
            execution.RunId,
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            _timestamp,
            _timestamp,
            null,
            "web",
            invocation.ModelSnapshot,
            binding.AdmissionOperationId,
            "embodysense.web",
            string.Empty,
            definition,
            invocation.TriggerPrompt,
            null,
            invocationContext,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admitted],
            null,
            null,
            null)
        {
            CapabilityAdmission = capabilityAdmission,
            SequentialInvocationSnapshot = invocation,
            SequentialAdapterBinding = binding,
            Frontier = CreateInitialFrontier(binding, plan, admitted),
        };
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(run).Errors));
        return new SequentialContext(run, invocation, binding, anchor, plan);
    }

    private static async Task<RetryCapacityStages> CreateCheckpointedRetryStagesAsync(
        WorkspacePaths paths,
        string identity,
        string? attachmentDetail = null,
        CustomLoopContextBlock[]? admissionContextBlocks = null,
        bool permitCapacityRefusal = false)
    {
        var context = CreateContext(RetryGraph(), identity: identity, admissionContextBlocks: admissionContextBlocks);
        var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);

        var selection = GovernedLoopSequentialFrontierMachine.Select(context.Run.Frontier, context.Binding, context.Plan);
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(selection.Node);
        var activation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(selection.Activation);
        var start = WithEvidence(
            Event(3, "retry-attempt-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var runningFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            context.Run.Frontier,
            context.Binding,
            context.Plan,
            node,
            activation,
            1,
            start.EventId,
            start.TimestampUtc).Frontier);
        var running = context.Run with
        {
            LifecycleVersion = 2,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = start.TimestampUtc,
            ExecutionClock = new CustomLoopExecutionClock(0, start.TimestampUtc),
            Frontier = runningFrontier,
            Events = [
                .. context.Run.Events,
                RetryLifecycleEvent(2, start.TimestampUtc, "Ordered execution started one exact retryable provider attempt."),
                start,
            ],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, 1)).Status);

        var failure = RetryableFailureEvent(
            Event(4, "retry-attempt-failed", CustomLoopRunEventKind.NodeAttemptFailed, "step-1", 1),
            context.Binding,
            new GovernedLoopFailureEvidenceReference(start.EventId, start.SequentialNodeEvidence!.EvidenceHash));
        var failed = running with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = failure.TimestampUtc,
            Events = [.. running.Events, failure],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(failed, 2)).Status);

        var policy = Assert.IsType<GovernedLoopRetryPolicy>(node.RetryPolicy);
        var retainedFailure = Assert.IsType<GovernedLoopFailureEvidence>(failure.FailureEvidence);
        var series = GovernedLoopRetryContract.CreateSeries(policy, retainedFailure, start.TimestampUtc);
        var budget = new GovernedLoopRetryBudgetSnapshot(1, null, null, null, null, 1);
        var retryOperationId = GovernedLoopRetryContract.CreateAttemptOperationId(series.SeriesId, 2);
        var eligibleAtUtc = failure.TimestampUtc.Add(GovernedLoopRetryContract.ComputeDelay(policy, series.SeriesId, 2));
        var retained = GovernedLoopRetryContract.CreateState(
            series,
            1,
            GovernedLoopRetryStateDisposition.FailureRetained,
            1,
            start.EventId,
            null,
            null,
            budget,
            null,
            null,
            null,
            retainedFailure.EvidenceId,
            retainedFailure.ContentHash,
            failure.TimestampUtc);
        var scheduled = GovernedLoopRetryContract.CreateState(
            series,
            2,
            GovernedLoopRetryStateDisposition.Scheduled,
            1,
            start.EventId,
            2,
            retryOperationId,
            budget,
            eligibleAtUtc,
            null,
            null,
            retainedFailure.EvidenceId,
            retainedFailure.ContentHash,
            failure.TimestampUtc);
        var parked = GovernedLoopSequentialFrontierMachine.ParkRunningForRetry(
            failed.Frontier,
            context.Binding,
            context.Plan,
            node,
            runningFrontier.Payload.Nodes[activation.ActivationOrdinal],
            1,
            2,
            retryOperationId,
            failure.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, parked.Status);
        var waitingFrontier = Assert.IsType<GovernedLoopFrontierPosture>(parked.Frontier);
        var scheduledRun = failed with
        {
            LifecycleVersion = 4,
            Status = CustomLoopRunStatus.Waiting,
            UpdatedAtUtc = failure.TimestampUtc,
            ExecutionClock = new CustomLoopExecutionClock(60_000, null),
            Frontier = waitingFrontier,
            Events = [
                .. failed.Events,
                RetryStateEvent(5, retained),
                RetryStateEvent(6, scheduled),
                RetryLifecycleEvent(7, failure.TimestampUtc, "Ordered execution entered Waiting for one exact bounded retry."),
            ],
        };
        var scheduledUpdateStatus = (await store.UpdateAsync(scheduledRun, 3)).Status;
        if (scheduledUpdateStatus == CustomLoopRunStoreStatus.LimitExceeded && permitCapacityRefusal)
        {
            return new RetryCapacityStages(store, context, activation, scheduledRun, scheduledRun, scheduled, eligibleAtUtc, scheduledUpdateStatus);
        }

        Assert.Equal(CustomLoopRunStoreStatus.Updated, scheduledUpdateStatus);

        var waitingActivation = waitingFrontier.Payload.Nodes[activation.ActivationOrdinal];
        var attachedAtUtc = failure.TimestampUtc.AddTicks(1);
        var checkpoint = GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            1,
            string.Empty,
            new GovernedLoopSleepBinding(
                context.Binding.ExecutionBinding,
                context.Binding.AdmissionReceipt.Intent.Publication,
                waitingFrontier.Payload.FrontierVersion,
                waitingFrontier.Payload.ContentHash,
                waitingActivation.ActivationOrdinal,
                waitingActivation.CycleId,
                waitingActivation.CycleIteration,
                waitingActivation.NodeId,
                waitingActivation.VisitOrdinal,
                waitingActivation.Attempt!.Value,
                waitingActivation.AttemptOperationId!),
            GovernedLoopWakeMode.Timestamp,
            scheduled.NextRetryAtUtc,
            null,
            attachedAtUtc,
            string.Empty));
        var attached = GovernedLoopRetryContract.CreateState(
            scheduled.Identity,
            3,
            GovernedLoopRetryStateDisposition.Scheduled,
            scheduled.CurrentAttempt,
            scheduled.CurrentAttemptOperationId,
            scheduled.NextAttempt,
            scheduled.AttemptOperationId,
            scheduled.Budget,
            scheduled.NextRetryAtUtc,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            scheduled.FailureEvidenceId,
            scheduled.FailureEvidenceHash,
            attachedAtUtc);
        var checkpointed = scheduledRun with
        {
            LifecycleVersion = 5,
            UpdatedAtUtc = attachedAtUtc,
            Events = [.. scheduledRun.Events, RetryStateEvent(8, attached) with { Detail = attachmentDetail ?? "Canonical retry-state transition." }],
        };
        var checkpointedUpdateStatus = (await store.UpdateAsync(checkpointed, 4)).Status;
        Assert.Equal(CustomLoopRunStoreStatus.Updated, checkpointedUpdateStatus);
        return new RetryCapacityStages(store, context, activation, scheduledRun, checkpointed, attached, eligibleAtUtc, checkpointedUpdateStatus);
    }

    private static WaitRunStages CreateWaitStages()
    {
        var context = CreateContext(WaitGraph());
        var admitted = context.Run;
        var inferenceSelection = GovernedLoopSequentialFrontierMachine.Select(admitted.Frontier, context.Binding, context.Plan);
        var inferenceNode = Assert.IsType<GovernedLoopSequentialPlanNode>(inferenceSelection.Node);
        var inferenceReady = Assert.IsType<GovernedLoopNodeExecutionEvidence>(inferenceSelection.Activation);
        var runningEvent = Event(2, "event-running", CustomLoopRunEventKind.LifecycleChanged);
        var inferenceStart = WithWaitEvidence(
            Event(3, "inference-attempt-1", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            inferenceReady.ActivationOrdinal,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            "step-to-wait");
        var inferenceStartedTransition = GovernedLoopSequentialFrontierMachine.Start(
            admitted.Frontier,
            context.Binding,
            context.Plan,
            inferenceNode,
            inferenceReady,
            1,
            inferenceStart.EventId,
            inferenceStart.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, inferenceStartedTransition.Status);
        var inferenceRunningFrontier = Assert.IsType<GovernedLoopFrontierPosture>(inferenceStartedTransition.Frontier);
        var inferenceRunning = admitted with
        {
            LifecycleVersion = 2,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = inferenceStart.TimestampUtc,
            ExecutionClock = new CustomLoopExecutionClock(0, inferenceStart.TimestampUtc),
            Frontier = inferenceRunningFrontier,
            Events = [.. admitted.Events, runningEvent, inferenceStart],
        };

        var inferenceCompletion = WithWaitEvidence(
            Event(4, "inference-completed-1", CustomLoopRunEventKind.NodeAttemptCompleted, "step-1", 1),
            context.Binding,
            inferenceReady.ActivationOrdinal,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            "step-to-wait");
        var inferenceCompletedTransition = GovernedLoopSequentialFrontierMachine.CompleteRunning(
            inferenceRunningFrontier,
            context.Binding,
            context.Plan,
            inferenceNode,
            inferenceRunningFrontier.Payload.Nodes[inferenceReady.ActivationOrdinal],
            1,
            inferenceStart.EventId,
            inferenceCompletion.EventId,
            inferenceCompletion.SequentialNodeEvidence!.OutcomeArtifactHash,
            GovernedLoopControlCondition.Success,
            [],
            inferenceCompletion.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, inferenceCompletedTransition.Status);
        var readyForWaitFrontier = Assert.IsType<GovernedLoopFrontierPosture>(inferenceCompletedTransition.Frontier);
        var readyForWait = inferenceRunning with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = inferenceCompletion.TimestampUtc,
            Frontier = readyForWaitFrontier,
            Events = [.. inferenceRunning.Events, inferenceCompletion],
        };

        var waitSelection = GovernedLoopSequentialFrontierMachine.Select(readyForWaitFrontier, context.Binding, context.Plan);
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(waitSelection.Node);
        var ready = Assert.IsType<GovernedLoopNodeExecutionEvidence>(waitSelection.Activation);
        var start = WithWaitEvidence(
            Event(5, "wait-attempt-1", CustomLoopRunEventKind.NodeAttemptStarted, "wait-1", 1),
            context.Binding,
            ready.ActivationOrdinal,
            "wait-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            "wait-to-exit");
        var startedTransition = GovernedLoopSequentialFrontierMachine.Start(
            readyForWaitFrontier,
            context.Binding,
            context.Plan,
            node,
            ready,
            1,
            start.EventId,
            start.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, startedTransition.Status);
        var runningFrontier = Assert.IsType<GovernedLoopFrontierPosture>(startedTransition.Frontier);
        var running = readyForWait with
        {
            LifecycleVersion = 4,
            UpdatedAtUtc = start.TimestampUtc,
            Frontier = runningFrontier,
            Events = [.. readyForWait.Events, start],
        };

        var parkedAtUtc = _timestamp.AddMinutes(5);
        var runningActivation = runningFrontier.Payload.Nodes[ready.ActivationOrdinal];
        var parkedTransition = GovernedLoopSequentialFrontierMachine.ParkRunning(
            runningFrontier,
            context.Binding,
            context.Plan,
            node,
            runningActivation,
            1,
            start.EventId,
            parkedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, parkedTransition.Status);
        var waitingFrontier = Assert.IsType<GovernedLoopFrontierPosture>(parkedTransition.Frontier);
        Assert.True(GovernedLoopWaitContractValidator.TryCreateCondition(
            node.Descriptor,
            node.Parameters,
            out var condition,
            out var conditionValidation));
        Assert.True(conditionValidation.IsValid);
        var wait = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitExecutionEvidence(
            1,
            runningActivation.ActivationOrdinal,
            runningActivation.NodeId,
            runningActivation.VisitOrdinal,
            runningActivation.CycleId,
            runningActivation.CycleIteration,
            runningActivation.Attempt!.Value,
            runningActivation.AttemptOperationId!,
            condition!,
            parkedAtUtc,
            waitingFrontier.Payload.FrontierVersion,
            waitingFrontier.Payload.ContentHash,
            null,
            null,
            string.Empty));
        var waitingEvent = Event(6, "event-waiting", CustomLoopRunEventKind.LifecycleChanged);
        var waiting = running with
        {
            LifecycleVersion = 5,
            Status = CustomLoopRunStatus.Waiting,
            UpdatedAtUtc = parkedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(60_000, null),
            Frontier = waitingFrontier,
            WaitEvidence = [wait],
            Events = [.. running.Events, waitingEvent],
        };

        var publishedAtUtc = parkedAtUtc.AddTicks(1);
        var sleepBinding = new GovernedLoopSleepBinding(
            context.Binding.ExecutionBinding,
            context.Binding.AdmissionReceipt.Intent.Publication,
            waitingFrontier.Payload.FrontierVersion,
            waitingFrontier.Payload.ContentHash,
            wait.ActivationOrdinal,
            wait.CycleId,
            wait.CycleIteration,
            wait.NodeId,
            wait.NodeVisitOrdinal,
            wait.WaitAttempt,
            wait.WaitOperationId);
        var checkpoint = GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            1,
            string.Empty,
            sleepBinding,
            GovernedLoopWakeMode.Timestamp,
            condition!.WakeDeadlineUtc,
            null,
            publishedAtUtc,
            string.Empty));
        var parkEvidence = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(
            1,
            condition,
            checkpoint,
            parkedAtUtc,
            string.Empty));
        var checkpointedWait = GovernedLoopWaitContractHash.Apply(wait with
        {
            ParkEvidence = parkEvidence,
            ContentHash = string.Empty,
        });
        var checkpointed = waiting with
        {
            LifecycleVersion = 6,
            UpdatedAtUtc = publishedAtUtc,
            WaitEvidence = [checkpointedWait],
        };

        var resumedAtUtc = condition.WakeDeadlineUtc!.Value;
        var wakeIdentity = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            1,
            string.Empty,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            checkpoint.WakeMode,
            checkpoint.AuthenticatedEventReference,
            null,
            string.Empty));
        var preparedWake = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            1,
            1,
            wakeIdentity,
            GovernedLoopWakeDisposition.Prepared,
            "continue-wait-1",
            null,
            null,
            resumedAtUtc,
            string.Empty));
        var resumedTransition = GovernedLoopSequentialFrontierMachine.ResumeWaiting(
            waitingFrontier,
            context.Binding,
            context.Plan,
            waitingFrontier.Payload.Nodes[wait.ActivationOrdinal],
            wait.WaitAttempt,
            wait.WaitOperationId,
            resumedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, resumedTransition.Status);
        var resumedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(resumedTransition.Frontier);
        var continuation = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitContinuationEvidence(
            1,
            parkEvidence.ContentHash,
            preparedWake,
            waitingFrontier.Payload.FrontierVersion,
            waitingFrontier.Payload.ContentHash,
            resumedFrontier.Payload.FrontierVersion,
            resumedFrontier.Payload.ContentHash,
            resumedAtUtc,
            string.Empty));
        var continuedWait = GovernedLoopWaitContractHash.Apply(checkpointedWait with
        {
            ContinuationEvidence = continuation,
            ContentHash = string.Empty,
        });
        var resumedEvent = Event(7, "event-resumed", CustomLoopRunEventKind.LifecycleChanged) with
        {
            TimestampUtc = resumedAtUtc,
        };
        var continued = checkpointed with
        {
            LifecycleVersion = 7,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = resumedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(checkpointed.ExecutionClock.AccumulatedRunningMilliseconds, resumedAtUtc),
            Frontier = resumedFrontier,
            WaitEvidence = [continuedWait],
            Events = [.. checkpointed.Events, resumedEvent],
        };
        var skippedParkPhase = continued with { LifecycleVersion = 6 };

        var completionSeed = Event(8, "event-wait-completed", CustomLoopRunEventKind.NodeAttemptCompleted, "wait-1", 1) with
        {
            WaitContinuationEvidenceHash = continuation.ContentHash,
        };
        var completion = WithWaitEvidence(
            completionSeed,
            context.Binding,
            wait.ActivationOrdinal,
            "wait-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            "wait-to-exit");
        var completedTransition = GovernedLoopSequentialFrontierMachine.CompleteRunning(
            resumedFrontier,
            context.Binding,
            context.Plan,
            node,
            resumedFrontier.Payload.Nodes[wait.ActivationOrdinal],
            wait.WaitAttempt,
            wait.WaitOperationId,
            completion.EventId,
            completion.SequentialNodeEvidence!.OutcomeArtifactHash,
            completion.SequentialNodeEvidence.ControlOutcome!.Value,
            [],
            completion.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, completedTransition.Status);
        var completed = continued with
        {
            LifecycleVersion = 8,
            UpdatedAtUtc = completion.TimestampUtc,
            Frontier = completedTransition.Frontier!,
            Events = [.. continued.Events, completion],
        };

        foreach (var run in new[] { admitted, inferenceRunning, readyForWait, running, waiting, checkpointed, skippedParkPhase, continued, completed })
        {
            var validation = CustomLoopRunValidator.Validate(run);
            Assert.True(validation.IsValid, $"Lifecycle {run.LifecycleVersion}:{Environment.NewLine}{string.Join(Environment.NewLine, validation.Errors)}");
        }

        return new WaitRunStages(admitted, inferenceRunning, readyForWait, running, waiting, checkpointed, skippedParkPhase, continued, completed);
    }

    private static HumanInputRunStages CreateMaximumHumanInputWaitingStages()
    {
        var configuration = MaximumHumanInputConfiguration();
        var context = CreateContext(MaximumHumanInputGraph(configuration), identity: "maximum-human-input");
        var admitted = context.Run;
        var inferenceSelection = GovernedLoopSequentialFrontierMachine.Select(admitted.Frontier, context.Binding, context.Plan);
        var inferenceNode = Assert.IsType<GovernedLoopSequentialPlanNode>(inferenceSelection.Node);
        var inferenceReady = Assert.IsType<GovernedLoopNodeExecutionEvidence>(inferenceSelection.Activation);
        var runningEvent = Event(2, "maximum-human-input-running", CustomLoopRunEventKind.LifecycleChanged);
        var inferenceStart = WithEvidence(
            Event(3, "maximum-human-input-inference-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            inferenceNode.NodeId,
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            activationOrdinalOverride: inferenceReady.ActivationOrdinal,
            outgoingControlEdgeIdsOverride: ["step-to-human-input"]);
        var inferenceStartedTransition = GovernedLoopSequentialFrontierMachine.Start(
            admitted.Frontier,
            context.Binding,
            context.Plan,
            inferenceNode,
            inferenceReady,
            1,
            inferenceStart.EventId,
            inferenceStart.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, inferenceStartedTransition.Status);
        var inferenceRunningFrontier = Assert.IsType<GovernedLoopFrontierPosture>(inferenceStartedTransition.Frontier);
        var inferenceRunning = admitted with
        {
            LifecycleVersion = 2,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = inferenceStart.TimestampUtc,
            ExecutionClock = new CustomLoopExecutionClock(0, inferenceStart.TimestampUtc),
            Frontier = inferenceRunningFrontier,
            Events = [.. admitted.Events, runningEvent, inferenceStart],
        };

        var inferenceCompletion = WithEvidence(
            Event(4, "maximum-human-input-inference-completed", CustomLoopRunEventKind.NodeAttemptCompleted, "step-1", 1),
            context.Binding,
            inferenceNode.NodeId,
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            activationOrdinalOverride: inferenceReady.ActivationOrdinal,
            outgoingControlEdgeIdsOverride: ["step-to-human-input"]);
        var inferenceCompletedTransition = GovernedLoopSequentialFrontierMachine.CompleteRunning(
            inferenceRunningFrontier,
            context.Binding,
            context.Plan,
            inferenceNode,
            inferenceRunningFrontier.Payload.Nodes[inferenceReady.ActivationOrdinal],
            1,
            inferenceStart.EventId,
            inferenceCompletion.EventId,
            inferenceCompletion.SequentialNodeEvidence!.OutcomeArtifactHash,
            GovernedLoopControlCondition.Success,
            [],
            inferenceCompletion.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, inferenceCompletedTransition.Status);
        var humanInputReadyFrontier = Assert.IsType<GovernedLoopFrontierPosture>(inferenceCompletedTransition.Frontier);
        var humanInputReady = inferenceRunning with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = inferenceCompletion.TimestampUtc,
            Frontier = humanInputReadyFrontier,
            Events = [.. inferenceRunning.Events, inferenceCompletion],
        };

        var humanInputSelection = GovernedLoopSequentialFrontierMachine.Select(humanInputReadyFrontier, context.Binding, context.Plan);
        var humanInputNode = Assert.IsType<GovernedLoopSequentialPlanNode>(humanInputSelection.Node);
        var humanInputReadyActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(humanInputSelection.Activation);
        Assert.True(GovernedLoopSequentialNodeDescriptors.IsHumanInput(humanInputNode.Descriptor));
        const string HumanInputClaimOperationId = "maximum-human-input-claim";
        var parkedAtUtc = _timestamp.AddMinutes(4);
        var humanInputStartedTransition = GovernedLoopSequentialFrontierMachine.Start(
            humanInputReadyFrontier,
            context.Binding,
            context.Plan,
            humanInputNode,
            humanInputReadyActivation,
            1,
            HumanInputClaimOperationId,
            parkedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, humanInputStartedTransition.Status);
        var humanInputRunningFrontier = Assert.IsType<GovernedLoopFrontierPosture>(humanInputStartedTransition.Frontier);
        var humanInputRunning = humanInputReady with
        {
            LifecycleVersion = 4,
            UpdatedAtUtc = parkedAtUtc,
            Frontier = humanInputRunningFrontier,
        };

        var humanInputRunningActivation = humanInputRunningFrontier.Payload.Nodes[humanInputReadyActivation.ActivationOrdinal];
        var humanInputParkedTransition = GovernedLoopSequentialFrontierMachine.ParkRunningHumanInput(
            humanInputRunningFrontier,
            context.Binding,
            context.Plan,
            humanInputNode,
            humanInputRunningActivation,
            1,
            HumanInputClaimOperationId,
            parkedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, humanInputParkedTransition.Status);
        var waitingFrontier = Assert.IsType<GovernedLoopFrontierPosture>(humanInputParkedTransition.Frontier);
        var checkpoint = CreateMaximumHumanInputWaitingCheckpoint(
            context.Binding,
            humanInputRunning,
            humanInputNode,
            waitingFrontier.Payload.Nodes[humanInputRunningActivation.ActivationOrdinal],
            waitingFrontier,
            configuration,
            parkedAtUtc);
        var waiting = humanInputRunning with
        {
            LifecycleVersion = 5,
            Status = CustomLoopRunStatus.Waiting,
            UpdatedAtUtc = parkedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(120_000, null),
            Frontier = waitingFrontier,
            HumanInputWaitingCheckpoints = [checkpoint],
            Events = [.. humanInputRunning.Events, Event(5, "maximum-human-input-waiting", CustomLoopRunEventKind.LifecycleChanged)],
        };

        foreach (var run in new[] { admitted, inferenceRunning, humanInputReady, humanInputRunning, waiting })
        {
            var validation = CustomLoopRunValidator.Validate(run);
            Assert.True(validation.IsValid, $"Lifecycle {run.LifecycleVersion}:{Environment.NewLine}{string.Join(Environment.NewLine, validation.Errors)}");
        }

        return new HumanInputRunStages(admitted, inferenceRunning, humanInputReady, humanInputRunning, waiting, configuration);
    }

    private static GovernedLoopHumanInputWaitingCheckpoint CreateMaximumHumanInputWaitingCheckpoint(
        GovernedLoopSequentialAdapterBinding binding,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopFrontierPosture waitingFrontier,
        GovernedLoopHumanInputNodeConfiguration configuration,
        DateTimeOffset resolvedAtUtc)
    {
        var timeout = HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            HumanInputPolicyArtifact.CurrentSchemaVersion,
            "timeout-policy-one",
            "revision-one",
            HumanInputPolicyKind.ResponseWindow,
            binding.WorkspaceId,
            binding.ExecutionBinding.Revision.GraphId,
            run.AdmissionActor,
            60_000,
            HumanInputTerminalDisposition.Unknown,
            string.Empty));
        var failure = HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            HumanInputPolicyArtifact.CurrentSchemaVersion,
            "failure-policy-one",
            "revision-one",
            HumanInputPolicyKind.DeadlineDisposition,
            binding.WorkspaceId,
            binding.ExecutionBinding.Revision.GraphId,
            run.AdmissionActor,
            null,
            HumanInputTerminalDisposition.Expired,
            string.Empty));
        var resolution = Assert.IsType<HumanInputPolicyResolutionSnapshot>(HumanInputPolicyResolutionSnapshot.TryCreate(
            binding.WorkspaceId,
            binding.ExecutionBinding.Revision.GraphId,
            binding.ExecutionBinding.Revision.RevisionId,
            node.NodeId,
            run.AdmissionActor,
            timeout,
            failure,
            resolvedAtUtc));
        var checkpointId = "maximum-human-input-checkpoint";
        var request = HumanInputRequestHash.Apply(new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            "maximum-human-input-request",
            "maximum-human-input-request-version",
            new HumanInputRequestBinding(
                binding.WorkspaceId,
                binding.ExecutionBinding.Revision.GraphId,
                binding.ExecutionBinding.Revision.RevisionId,
                node.NodeId,
                run.Id,
                checkpointId),
            configuration.Purpose!,
            configuration.Prompt!,
            configuration.ResponseSchema!,
            configuration.PrivacyClass,
            configuration.EligibleRespondents!.Select(item => item!).ToArray(),
            new HumanInputTiming(resolution.ResolvedAtUtc, resolution.ExpiresAtUtc),
            configuration.ResponsePolicy!,
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, node.NodeId, checkpointId),
            string.Empty));
        var evidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion,
            1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published,
            resolvedAtUtc,
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            string.Empty));
        var checkpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            new GovernedLoopHumanInputWaitingCheckpointBinding(
                GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion,
                binding.WorkspaceId,
                binding.ExecutionBinding,
                binding.AdmissionReceipt.Intent.Publication,
                binding.GraphArtifactHash,
                binding.GraphLayoutHash,
                binding.AdmissionReceiptHash,
                waitingFrontier.Payload.FrontierVersion,
                waitingFrontier.Payload.ContentHash,
                activation.ActivationOrdinal,
                activation.CycleId,
                activation.CycleIteration,
                node.NodeId,
                activation.VisitOrdinal,
                checkpointId),
            configuration,
            resolution,
            request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Pending,
            [evidence],
            string.Empty));
        Assert.True(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(checkpoint).IsValid);
        return checkpoint;
    }

    private static async Task PersistWaitStagesAsync(CustomLoopRunStore store, WaitRunStages stages)
    {
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(stages.Admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.InferenceRunning, stages.Admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.ReadyForWait, stages.InferenceRunning.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.WaitRunning, stages.ReadyForWait.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.Waiting, stages.WaitRunning.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.Checkpointed, stages.Waiting.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.Continued, stages.Checkpointed.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(stages.Completed, stages.Continued.LifecycleVersion)).Status);
    }

    private static async Task<CustomLoopRunRecord> CompleteCanonicalInferenceAsync(
        CustomLoopRunStore store,
        SequentialContext context)
    {
        var start = WithEvidence(
            Event(2, "event-inference-start", CustomLoopRunEventKind.NodeAttemptStarted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var selection = GovernedLoopSequentialFrontierMachine.Select(context.Run.Frontier, context.Binding, context.Plan);
        var startedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            context.Run.Frontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[1],
            selection.Activation,
            1,
            start.EventId,
            start.TimestampUtc).Frontier);
        var started = context.Run with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = _timestamp.AddMinutes(1),
            Frontier = startedFrontier,
            Events = [.. context.Run.Events, start],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(started, 1)).Status);
        var completion = WithEvidence(
            Event(3, "event-inference-completed", CustomLoopRunEventKind.NodeAttemptCompleted, "step-1", 1),
            context.Binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var completedFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.CompleteRunning(
            startedFrontier,
            context.Binding,
            context.Plan,
            context.Plan.Nodes[1],
            startedFrontier.Payload.Nodes[1],
            1,
            start.EventId,
            completion.EventId,
            completion.SequentialNodeEvidence!.OutcomeArtifactHash,
            GovernedLoopControlCondition.Success,
            [],
            completion.TimestampUtc).Frontier);
        var completed = started with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = _timestamp.AddMinutes(2),
            Frontier = completedFrontier,
            Events = [.. started.Events, completion],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(completed, 2)).Status);
        return completed;
    }

    private static CustomLoopRunRecord AsLegacyRun(CustomLoopRunRecord run)
    {
        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(run.AdmittedDefinition.CapabilityRequirements, _timestamp);
        return CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = capabilityAdmission,
            SequentialInvocationSnapshot = null,
            SequentialAdapterBinding = null,
            Frontier = null,
            Events = [run.Events[0] with { SequentialNodeEvidence = null }],
        });
    }

    private static GovernedLoopFrontierPosture CreateInitialFrontier(
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopSequentialPlan plan,
        CustomLoopRunEvent admitted)
    {
        var initialized = GovernedLoopSequentialFrontierMachine.Initialize(
            binding,
            plan,
            admitted.EventId,
            admitted.EventId,
            admitted.SequentialNodeEvidence!.OutcomeArtifactHash,
            admitted.TimestampUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, initialized.Status);
        return Assert.IsType<GovernedLoopFrontierPosture>(initialized.Frontier);
    }

    private static CustomLoopRunRecord WithPureFrontier(CustomLoopRunRecord run)
    {
        var current = run.Frontier!;
        var source = current.Payload.Nodes[1];
        var pure = GovernedLoopNodeExecutionEvidence.Create(
            source.PlanOrdinal,
            source.NodeId,
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "identity", 1),
            source.IncomingControlEdgeIds,
            source.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Ready);
        return run with { Frontier = RebuildFrontier(current, current.Payload.FrontierVersion, [current.Payload.Nodes[0], pure], current.Payload.UpdatedAtUtc) };
    }

    private static CustomLoopRunRecord WithPendingPureFrontier(CustomLoopRunRecord run, int pureNodeCount)
    {
        var current = run.Frontier!;
        var trigger = current.Payload.Nodes[0];
        var nodes = new List<GovernedLoopNodeExecutionEvidence> { trigger };
        for (var index = 0; index < pureNodeCount; index++)
        {
            var incoming = index == 0 ? trigger.OutgoingControlEdgeIds : [$"edge-pure-{index}-pure-{index + 1}"];
            var outgoing = index == pureNodeCount - 1 ? Array.Empty<string>() : [$"edge-pure-{index + 1}-pure-{index + 2}"];
            var status = index == 0 ? GovernedLoopNodeExecutionStatus.Ready : GovernedLoopNodeExecutionStatus.Waiting;
            nodes.Add(GovernedLoopNodeExecutionEvidence.Create(
                index + 1,
                $"pure-{index + 1}",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "identity", 1),
                incoming,
                outgoing,
                status,
                status == GovernedLoopNodeExecutionStatus.Waiting ? 1 : null,
                status == GovernedLoopNodeExecutionStatus.Waiting ? $"attempt-pure-{index + 1}-1" : null));
        }

        return run with { Frontier = RebuildFrontier(current, current.Payload.FrontierVersion, nodes, current.Payload.UpdatedAtUtc) };
    }

    private static GovernedLoopFrontierPosture StartPureFrontier(GovernedLoopFrontierPosture current, CustomLoopRunEvent start)
    {
        var source = current.Payload.Nodes[1];
        var running = GovernedLoopNodeExecutionEvidence.CreateActivation(
            source.ActivationOrdinal,
            source.PlanOrdinal,
            source.VisitOrdinal,
            source.NodeId,
            source.Descriptor,
            source.IncomingControlEdgeIds,
            source.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Running,
            1,
            start.EventId,
            cycleId: source.CycleId,
            cycleIteration: source.CycleIteration,
            joinArrivals: source.JoinArrivals);
        return RebuildFrontier(current, current.Payload.FrontierVersion + 1, [current.Payload.Nodes[0], running], start.TimestampUtc);
    }

    private static GovernedLoopFrontierPosture CompletePureFrontier(
        GovernedLoopFrontierPosture current,
        CustomLoopRunEvent start,
        CustomLoopRunEvent completion)
    {
        var source = current.Payload.Nodes[1];
        var evidence = completion.SequentialNodeEvidence!;
        var completed = GovernedLoopNodeExecutionEvidence.CreateActivation(
            source.ActivationOrdinal,
            source.PlanOrdinal,
            source.VisitOrdinal,
            source.NodeId,
            source.Descriptor,
            source.IncomingControlEdgeIds,
            source.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            start.EventId,
            completion.EventId,
            evidence.OutcomeArtifactHash,
            source.CycleId,
            source.CycleIteration,
            evidence.ControlOutcome,
            evidence.SelectedControlEdgeIds,
            evidence.SkippedControlEdgeIds,
            source.JoinArrivals);
        var exit = GovernedLoopNodeExecutionEvidence.CreateActivation(
            2,
            2,
            1,
            "exit",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
            source.OutgoingControlEdgeIds,
            [],
            GovernedLoopNodeExecutionStatus.Ready);
        return RebuildFrontier(current, current.Payload.FrontierVersion + 1, [current.Payload.Nodes[0], completed, exit], completion.TimestampUtc);
    }

    private static GovernedLoopFrontierPosture RebuildFrontier(
        GovernedLoopFrontierPosture current,
        long frontierVersion,
        IEnumerable<GovernedLoopNodeExecutionEvidence> nodes,
        DateTimeOffset updatedAtUtc)
    {
        var payload = GovernedLoopFrontierPayload.Create(
            1,
            frontierVersion,
            current.Payload.ConcurrencyCeiling,
            GovernedLoopFrontierStatus.Active,
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

    private static string[] ControlEdges(string? edgeId)
        => edgeId is null ? [] : [edgeId];

    internal static GovernedLoopGraphDefinition LinearGraph(bool scheduleTrigger = false)
    {
        var role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a'));
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                scheduleTrigger ? GovernedLoopSequentialNodeDescriptors.ScheduleTrigger : GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create(scheduleTrigger ? [ScheduleTriggerCapabilityId] : []),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "step-1",
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId, ModelProfileCapabilityId]),
                new Dictionary<string, string> { ["instruction"] = "Answer safely." }),
            new GovernedLoopNodeDefinition(
                "exit",
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()),
        };
        var edges = new[]
        {
            new GovernedLoopControlEdgeDefinition("trigger-to-step", "trigger", "step-1", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("step-to-exit", "step-1", "exit", GovernedLoopControlCondition.Success),
        };
        var bindings = new[]
        {
            new GovernedLoopBindingDefinition("data-to-step", GovernedLoopBindingKind.Data, "trigger", "request", "step-1", "request"),
            new GovernedLoopBindingDefinition("context-to-step", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "step-1", "invocation-context"),
            new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "step-1", "result", "exit", "result"),
        };
        return GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Execute one exact supported sequential governed graph.",
            role,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create(scheduleTrigger
                ? [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, ScheduleTriggerCapabilityId]
                : [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            edges,
            bindings,
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Sequential loop",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            DefaultModelRoutingPolicy());
    }

    private static GovernedLoopGraphDefinition HumanReviewGraph() => HumanReviewOrderedReleaseGraphFixture.Graph();

    private static GovernedLoopGraphDefinition MaximumHumanInputGraph(GovernedLoopHumanInputNodeConfiguration configuration)
    {
        var role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a'));
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "step-1",
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId, ModelProfileCapabilityId]),
                new Dictionary<string, string> { ["instruction"] = "Collect the bounded Human Input request." }),
            new GovernedLoopNodeDefinition(
                "human-input",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                [Port(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>(),
                null,
                null,
                null,
                configuration),
            new GovernedLoopNodeDefinition(
                "exit",
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()),
        };
        return GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Persist one exact maximum bounded Human Input waiting checkpoint.",
            role,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-step", "trigger", "step-1", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("step-to-human-input", "step-1", "human-input", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-input-to-exit", "human-input", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("data-to-step", GovernedLoopBindingKind.Data, "trigger", "request", "step-1", "request"),
                new GovernedLoopBindingDefinition("context-to-step", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "step-1", "invocation-context"),
                new GovernedLoopBindingDefinition("response-to-exit", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "exit", "result"),
            ],
            new GovernedLoopOutputContract("Return the exact bounded Human Input response.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Maximum Human Input loop",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            DefaultModelRoutingPolicy());
    }

    private static GovernedLoopHumanInputNodeConfiguration MaximumHumanInputConfiguration()
        => new(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            "text",
            "Collect one bounded private response.",
            new string('p', HumanInputLimits.MaxPromptCharacters),
            new HumanInputResponseSchema(
                HumanInputResponseKind.Choice,
                null,
                Enumerable.Range(0, HumanInputLimits.MaxChoices)
                    .Select(index => new HumanInputChoice(
                        "choice-" + index.ToString("D2", CultureInfo.InvariantCulture) + "-" + new string('a', HumanInputLimits.MaxIdentifierCharacters - 10),
                        "Display " + index.ToString("D2", CultureInfo.InvariantCulture) + " " + new string('x', HumanInputLimits.MaxChoiceDisplayCharacters - 11)))
                    .ToArray(),
                null,
                null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("actor-one", "role-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one@revision-one",
            "failure-policy-one@revision-one");

    private static GovernedLoopGraphDefinition WaitGraph()
    {
        var role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a'));
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "step-1",
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId, ModelProfileCapabilityId]),
                new Dictionary<string, string> { ["instruction"] = "Answer safely before waiting." }),
            new GovernedLoopNodeDefinition(
                "wait-1",
                GovernedLoopSequentialNodeDescriptors.TimestampWait,
                [],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = _timestamp.AddMinutes(7).ToString(
                        GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat,
                        CultureInfo.InvariantCulture),
                }),
            new GovernedLoopNodeDefinition(
                "exit",
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()),
        };
        return GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Execute one exact supported sequential governed Wait graph.",
            role,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-step", "trigger", "step-1", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("step-to-wait", "step-1", "wait-1", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("wait-to-exit", "wait-1", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("data-to-step", GovernedLoopBindingKind.Data, "trigger", "request", "step-1", "request"),
                new GovernedLoopBindingDefinition("context-to-step", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "step-1", "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "step-1", "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Wait loop",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            DefaultModelRoutingPolicy());
    }

    private static GovernedLoopGraphDefinition RetryGraph()
    {
        var source = LinearGraph();
        var inference = source.Nodes.Single(node => node.Id == "step-1");
        var retryInference = new GovernedLoopNodeDefinition(
            inference.Id,
            inference.Descriptor,
            inference.Ports,
            inference.AuthorityCeiling,
            inference.Parameters,
            inference.ModelRoutingPolicy,
            inference.AuthoredInputDataClasses,
            RetryPolicy(inference.Id));
        return GovernedLoopGraphDefinition.Create(
            source.SchemaVersion,
            source.GraphId,
            source.RevisionId,
            source.Purpose,
            source.OwningRole,
            source.EntryNodeId,
            source.TerminalNodeIds,
            source.AuthorityCeiling,
            source.ValueSchemas,
            source.Nodes.Select(node => node.Id == inference.Id ? retryInference : node),
            source.ControlEdges,
            source.Bindings,
            source.OutputContract,
            source.DisplayMetadata,
            source.DefaultModelRoutingPolicy);
    }

    private static GovernedLoopGraphDefinition FailGraph()
    {
        var source = LinearGraph();
        var fail = new GovernedLoopNodeDefinition(
            "fail",
            GovernedLoopSequentialNodeDescriptors.FailTerminal,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return GovernedLoopGraphDefinition.Create(
            source.SchemaVersion,
            source.GraphId,
            source.RevisionId,
            "Route one classified provider failure into the canonical Fail terminal.",
            source.OwningRole,
            source.EntryNodeId,
            ["exit", "fail"],
            source.AuthorityCeiling,
            source.ValueSchemas,
            [.. source.Nodes, fail],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-step", "trigger", "step-1", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("step-to-exit", "step-1", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("step-to-fail", "step-1", "fail", GovernedLoopControlCondition.Failure),
            ],
            source.Bindings,
            source.OutputContract,
            new GovernedLoopDisplayMetadata(
                "Failure-routed loop",
                "Display metadata is not execution order.",
                [.. source.DisplayMetadata.Nodes, new GovernedLoopNodeDisplayMetadata("fail", "fail", "Node.", 300, 0)]),
            source.DefaultModelRoutingPolicy);
    }

    private static CapabilityAdmissionSnapshot SequentialCapabilityAdmission(string graphArtifactHash, bool scheduleTrigger = false)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-sequential", out var subject, out _));
        Assert.True(CapabilityId.TryParse(ConversationTurnCapabilityId, out var conversationTurn, out _));
        Assert.True(CapabilityId.TryParse(ModelInferenceCapabilityId, out var modelInference, out _));
        Assert.True(CapabilityId.TryParse(ScheduleTriggerCapabilityId, out var scheduleTriggerCapability, out _));
        Assert.True(CapabilityId.TryParse(ModelProfileCapabilityId, out var modelProfile, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var versions, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + graphArtifactHash, out var checksum, out _));
        var requirements = new CapabilityDependencyManifest(
            1,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            scheduleTrigger
                ? [new CapabilityDependency(conversationTurn!, versions!), new CapabilityDependency(modelInference!, versions!), new CapabilityDependency(modelProfile!, versions!), new CapabilityDependency(scheduleTriggerCapability!, versions!)]
                : [new CapabilityDependency(conversationTurn!, versions!), new CapabilityDependency(modelInference!, versions!), new CapabilityDependency(modelProfile!, versions!)],
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        var admission = TestCapabilityAdmissionFactory.Create(requirements, _timestamp);
        return admission;
    }

    private static CapabilityAdmissionSnapshot PreDispatchEffectCapabilityAdmission(string graphArtifactHash, string workspaceId)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-sequential", out var subject, out _));
        Assert.True(CapabilityId.TryParse(ConversationTurnCapabilityId, out var conversationTurn, out _));
        Assert.True(CapabilityId.TryParse(HumanReviewOrderedReleaseGraphFixture.WorkspaceCommandCapabilityId, out var workspaceCommand, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var versions, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + graphArtifactHash, out var checksum, out _));
        var requirements = new CapabilityDependencyManifest(
            1,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            [new CapabilityDependency(conversationTurn!, versions!), new CapabilityDependency(workspaceCommand!, versions!)],
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        var admission = TestCapabilityAdmissionFactory.Create(requirements, _timestamp);
        var descriptor = HumanReviewOrderedReleaseGraphFixture.WorkspaceCapability();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var failure), failure is null ? null : string.Join(", ", failure.Errors));
        var replacement = new CapabilityAdmissionPin(
            identity!,
            descriptor.Kind,
            descriptor.Implementation,
            descriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            descriptor.Purpose);
        return admission with
        {
            WorkspaceScopeId = workspaceId,
            Pins = admission.Pins.Select(pin => pin.DescriptorIdentity.Id.Equals(workspaceCommand) ? replacement : pin).ToArray(),
            Evidence = admission.Evidence.Select(item => item.DependencyId.Equals(workspaceCommand) ? item with { SelectedIdentity = identity } : item).ToArray(),
        };
    }

    private static GovernedModelRoutingAdmissionSnapshot ModelRoutingAdmission(
        GovernedLoopGraphDefinition graph,
        GovernedLoopAdmissionIntent intent,
        GovernedLoopExecutionBinding execution,
        AuthorityGrantProfilePin grantProfile,
        AuthorityGrantBoundary grantBoundary,
        string grantDependencyEvidenceHash,
        EmbodySense.Core.Common.Authority.Models.AuthorityCeiling effectiveAuthority,
        CapabilityAdmissionSnapshot capabilityAdmission,
        DateTimeOffset evaluatedAtUtc)
    {
        var capabilityPin = capabilityAdmission.Pins.Single(pin =>
            string.Equals(pin.DescriptorIdentity.Id.Value, ModelProfileCapabilityId, StringComparison.Ordinal));
        Assert.Equal(CapabilityKind.ModelProfile, capabilityPin.Kind);
        Assert.True(CapabilityDataClass.TryParse("public", out var publicData, out var dataClassError), dataClassError?.Message);
        var privacy = GovernedModelPrivacyPosture.Create(
            1,
            GovernedModelLocality.LocalProcess,
            CapabilityEgressMode.None,
            [],
            [publicData!],
            ["local"],
            GovernedModelRetentionPosture.None,
            GovernedModelTrainingPosture.Prohibited);
        var usageSupport = GovernedModelUsageSupportPolicy.Create(
            GovernedModelUsageSupport.Unavailable,
            GovernedModelUsageSupport.Unavailable,
            GovernedModelUsageSupport.Unavailable,
            GovernedModelUsageSupport.Unavailable,
            GovernedModelUsageSupport.Unavailable);
        var metadata = GovernedModelProfileMetadata.Create(
            1,
            capabilityPin.DescriptorIdentity,
            "org.embodysense",
            "provider-inference",
            "test-model",
            "local",
            1,
            Hash('6'),
            "Exact test model profile for the sequential persistence fixture.",
            [GovernedModelModality.Text],
            [],
            8_000,
            512,
            privacy,
            usageSupport,
            [graph.OwningRole.Identity.RoleId],
            ["provider-inference"]);
        var adapterRegistryRevisionHash = Hash('5');
        var profile = GovernedModelProfilePin.Create(
            capabilityPin,
            metadata,
            Hash('4'),
            adapterRegistryRevisionHash);
        var policy = graph.DefaultModelRoutingPolicy;
        Assert.Equal(capabilityPin.DescriptorIdentity.Id, policy.Selector.ExactProfileId);
        var node = graph.Nodes.Single(candidate => string.Equals(candidate.Id, "step-1", StringComparison.Ordinal));
        var entry = GovernedModelRoutingAdmissionEntry.Create(
            1,
            node.Id,
            node.Descriptor.TypeId,
            policy.ContentHash,
            policy.Requirements,
            false,
            [],
            profile,
            []);
        return GovernedModelRoutingAdmissionSnapshot.Create(
            1,
            intent.WorkspaceId,
            intent.OperationId,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            GovernedLoopAdmissionContractHash.ComputeExecutionBindingReferenceHash(execution),
            execution.RunId,
            execution.Revision.GraphId,
            execution.Revision.RevisionId,
            execution.Revision.ExecutableHash,
            execution.ExecutionGeneration,
            intent.Role.Identity.RoleId,
            intent.Role.Identity.Revision,
            intent.Role.ContentHash,
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(capabilityAdmission),
            GovernedLoopAdmissionContractHash.ComputeAdmissionAuthorityReferenceHash(
                grantProfile,
                grantBoundary,
                grantDependencyEvidenceHash,
                effectiveAuthority),
            1,
            null,
            null,
            adapterRegistryRevisionHash,
            evaluatedAtUtc,
            [entry]);
    }

    private static GovernedLoopPortDefinition Port(string id, GovernedLoopPortDirection direction, GovernedLoopBindingKind kind)
        => new(id, direction, kind, "text", true);

    private static GovernedModelRoutingPolicy DefaultModelRoutingPolicy()
    {
        var source = GovernedLoopGraphTestFixture.DefaultModelRoutingPolicy();
        Assert.True(CapabilityId.TryParse(ModelProfileCapabilityId, out var profileId, out _));
        return GovernedModelRoutingPolicy.Create(
            1,
            GovernedModelRoutingSelector.Exact(profileId!),
            source.FallbackProfileIds,
            source.Requirements);
    }

    private static GovernedLoopRetryPolicy RetryPolicy(string nodeId)
        => GovernedLoopRetryContract.CreatePolicy(
            "retry-capacity-policy",
            nodeId,
            [GovernedLoopFailureClass.DispatchProvedNotStarted],
            ["provider-dispatch-not-started"],
            2,
            1_000,
            600_000,
            GovernedLoopRetryBackoffStrategy.Fixed,
            1_000,
            1_000,
            GovernedLoopRetryJitterStrategy.None,
            0,
            null,
            null,
            null,
            null,
            2);

    private static CustomLoopRunEvent RetryableFailureEvent(CustomLoopRunEvent eventValue, GovernedLoopSequentialAdapterBinding binding, GovernedLoopFailureEvidenceReference causalEvidence)
    {
        var rejected = WithEvidence(
            eventValue,
            binding,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            CustomLoopSequentialNodeDisposition.Rejected,
            causalEvidence);
        var failure = GovernedLoopFailureEvidenceContract.Create(
            eventValue.EventId + "-failure",
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            1,
            1,
            "step-1",
            1,
            GovernedLoopFailureClass.DispatchProvedNotStarted,
            "provider-dispatch-not-started",
            GovernedLoopFailureSource.Provider,
            GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted,
            GovernedLoopFailureAuthorityPosture.Current,
            GovernedLoopFailureHumanPosture.None,
            GovernedLoopFailureRetrySafety.RetryableWithExactIntent,
            GovernedLoopFailureSeverity.Error,
            700,
            [causalEvidence],
            null,
            eventValue.TimestampUtc);
        var withFailure = rejected with { FailureEvidence = failure };
        var sequential = Assert.IsType<CustomLoopSequentialNodeEvidence>(rejected.SequentialNodeEvidence) with
        {
            OutcomeArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(withFailure),
            FailureEvidenceId = failure.EvidenceId,
            FailureEvidenceHash = failure.ContentHash,
        };
        return withFailure with { SequentialNodeEvidence = CustomLoopSequentialNodeEvidenceHash.Apply(sequential) };
    }

    private static CustomLoopRunEvent RetryStateEvent(long sequence, GovernedLoopRetryState state)
        => new(sequence, $"retry-{state.StateVersion}", state.RecordedAtUtc, CustomLoopRunEventKind.RetryStateChanged, 1, state.Identity.NodeId, state.CurrentAttempt, "Canonical retry-state transition.", [], null, null, null, null, null, null, null, null, null, null)
        {
            RetryState = state,
        };

    private static string MaximumRetryStateDetail()
    {
        var surrogatePair = char.ConvertFromUtf32(0x1F600);
        var detail = string.Concat(Enumerable.Repeat(surrogatePair, CustomLoopLimits.MaxRetryStateDetailCharacters / surrogatePair.Length));
        Assert.Equal(CustomLoopLimits.MaxRetryStateDetailCharacters, detail.Length);
        return detail;
    }

    private static CustomLoopContextBlock[] NearLimitRetryCapacityContextBlocks()
        => Enumerable.Range(0, 46).Select(index =>
        {
            var prefix = index.ToString("D2") + ":";
            var content = prefix + new string('x', CustomLoopLimits.MaxLogicalProviderRequestCharacters - prefix.Length);
            return new CustomLoopContextBlock(
                CustomLoopContextSource.HarnessGovernance,
                $"retry-capacity-{index:D2}",
                LlmMessageRole.System,
                true,
                null,
                content,
                CustomLoopTraceContentHash.Compute(content),
                content.Length,
                false,
                EmbodySenseDeveloperInstructions.CurrentVersion);
        }).ToArray();

    private static CustomLoopRunEvent RetryLifecycleEvent(long sequence, DateTimeOffset timestampUtc, string detail)
        => new(sequence, $"retry-lifecycle-{sequence}", timestampUtc, CustomLoopRunEventKind.LifecycleChanged, null, null, null, detail, [], null, null, null, null, null, null, null, null, null, null);

    private static GovernedLoopRetryState RetrySuccessor(
        GovernedLoopRetryState current,
        GovernedLoopRetryStateDisposition disposition,
        GovernedLoopRetryBudgetSnapshot budget,
        DateTimeOffset recordedAtUtc)
        => GovernedLoopRetryContract.CreateState(
            current.Identity,
            current.StateVersion + 1,
            disposition,
            current.CurrentAttempt,
            current.CurrentAttemptOperationId,
            current.NextAttempt,
            current.AttemptOperationId,
            budget,
            null,
            current.WakeCheckpointId,
            current.WakeCheckpointHash,
            current.FailureEvidenceId,
            current.FailureEvidenceHash,
            recordedAtUtc);

    private static GovernedLoopRetryState RetryTerminalSuccessor(
        GovernedLoopRetryState current,
        GovernedLoopRetryStateDisposition disposition,
        GovernedLoopRetryBudgetSnapshot budget,
        DateTimeOffset recordedAtUtc)
        => GovernedLoopRetryContract.CreateState(
            current.Identity,
            current.StateVersion + 1,
            disposition,
            current.CurrentAttempt,
            current.CurrentAttemptOperationId,
            null,
            null,
            budget,
            null,
            null,
            null,
            current.FailureEvidenceId,
            current.FailureEvidenceHash,
            recordedAtUtc);

    private static CustomLoopRunEvent RetryExhaustionEvent(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlan plan,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopRunEvent terminalStateEvent,
        GovernedLoopRetryState terminal,
        DateTimeOffset recordedAtUtc,
        string detail)
    {
        var binding = Assert.IsType<GovernedLoopSequentialAdapterBinding>(run.SequentialAdapterBinding);
        var selectedEdges = plan.ControlEdges
            .Where(edge => string.Equals(edge.FromNodeId, activation.NodeId, StringComparison.Ordinal)
                && edge.Condition == GovernedLoopControlCondition.Failure)
            .Select(edge => edge.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var skippedEdges = activation.OutgoingControlEdgeIds.Except(selectedEdges, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var failure = GovernedLoopFailureEvidenceContract.Create(
            $"retry-exhaustion-{terminal.Identity.SeriesId[..16]}-{terminal.StateVersion}",
            binding.WorkspaceId,
            run.Id,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt!.Value,
            GovernedLoopFailureClass.Exhaustion,
            detail,
            GovernedLoopFailureSource.Runtime,
            GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted,
            GovernedLoopFailureAuthorityPosture.Current,
            GovernedLoopFailureHumanPosture.None,
            GovernedLoopFailureRetrySafety.NotRetryable,
            GovernedLoopFailureSeverity.Error,
            900,
            [new GovernedLoopFailureEvidenceReference(terminalStateEvent.EventId, terminal.ContentHash)],
            "retry budget exhausted before dispatch",
            recordedAtUtc);
        var runEvent = new CustomLoopRunEvent(
            run.Events.Length + 1,
            failure.EvidenceId,
            recordedAtUtc,
            CustomLoopRunEventKind.NodeAttemptFailed,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The exact retry budget was exhausted before another attempt could dispatch.",
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
            null)
        {
            FailureEvidence = failure,
        };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            binding.WorkspaceId,
            run.Id,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt,
            activation.CycleId,
            activation.CycleIteration,
            GovernedLoopControlCondition.Failure,
            selectedEdges,
            skippedEdges,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Rejected,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty)
        {
            FailureEvidenceId = failure.EvidenceId,
            FailureEvidenceHash = failure.ContentHash,
        });
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent Event(
        long sequence,
        string eventId,
        CustomLoopRunEventKind kind,
        string? stepId = null,
        int? attempt = null)
        => new(
            sequence,
            eventId,
            _timestamp.AddMinutes(sequence - 1),
            kind,
            stepId is null ? null : 1,
            stepId,
            attempt,
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
            TraceReservationUtf8Bytes: kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted
                ? CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes
                : null);

    private static CustomLoopRunEvent WithEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        string nodeId,
        int attempt,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition,
        GovernedLoopFailureEvidenceReference? failureSource = null,
        int? activationOrdinalOverride = null,
        string[]? outgoingControlEdgeIdsOverride = null,
        string[]? selectedControlEdgeIdsOverride = null,
        string[]? skippedControlEdgeIdsOverride = null)
    {
        var activationOrdinal = activationOrdinalOverride ?? (string.Equals(nodeId, "trigger", StringComparison.Ordinal)
            ? 0
            : string.Equals(nodeId, "exit", StringComparison.Ordinal) ? 2 : 1);
        var controlOutcome = kind switch
        {
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted => (GovernedLoopControlCondition?)null,
            _ when string.Equals(nodeId, "trigger", StringComparison.Ordinal) => GovernedLoopControlCondition.Always,
            _ when disposition == CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopControlCondition.Failure,
            _ => GovernedLoopControlCondition.Success,
        };
        var outgoing = outgoingControlEdgeIdsOverride ?? nodeId switch
        {
            "trigger" => new[] { "trigger-to-step" },
            "step-1" => new[] { "step-to-exit" },
            _ => [],
        };
        var failure = disposition == CustomLoopSequentialNodeDisposition.Rejected
            ? GovernedLoopFailureEvidenceContract.Create(
                runEvent.EventId + "-failure",
                binding.WorkspaceId,
                binding.ExecutionBinding.RunId,
                binding.ExecutionBinding.Revision,
                binding.ExecutionBinding.ExecutionGeneration,
                activationOrdinal,
                1,
                nodeId,
                attempt,
                GovernedLoopFailureClass.ValidationConfiguration,
                "persistence-fixture-rejected",
                GovernedLoopFailureSource.Validation,
                GovernedLoopFailureEffectCertainty.NotApplicable,
                GovernedLoopFailureAuthorityPosture.NotApplicable,
                GovernedLoopFailureHumanPosture.None,
                GovernedLoopFailureRetrySafety.NotRetryable,
                GovernedLoopFailureSeverity.Error,
                700,
                [failureSource ?? throw new InvalidOperationException("Rejected sequential fixture evidence requires an exact causal source.")],
                null,
                runEvent.TimestampUtc)
            : null;
        var outcomeEvent = runEvent with { FailureEvidence = failure };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            1,
            kind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activationOrdinal,
            1,
            nodeId,
            attempt,
            null,
            null,
            controlOutcome,
            selectedControlEdgeIdsOverride ?? (controlOutcome is null or GovernedLoopControlCondition.Failure ? [] : outgoing),
            skippedControlEdgeIdsOverride ?? (controlOutcome == GovernedLoopControlCondition.Failure ? outgoing : []),
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

    private static CustomLoopRunEvent WithWaitEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        int activationOrdinal,
        string nodeId,
        int attempt,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition,
        string outgoingEdgeId)
    {
        var controlOutcome = kind == CustomLoopSequentialNodeEvidenceKind.DispatchStarted
            ? (GovernedLoopControlCondition?)null
            : GovernedLoopControlCondition.Success;
        var outgoing = new[] { outgoingEdgeId };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            1,
            kind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activationOrdinal,
            1,
            nodeId,
            attempt,
            null,
            null,
            controlOutcome,
            controlOutcome is null ? [] : outgoing,
            [],
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent PureEvent(
        long sequence,
        string eventId,
        CustomLoopRunEventKind kind,
        GovernedLoopSequentialAdapterBinding binding,
        string? outcomeJson = null)
    {
        var runEvent = Event(sequence, eventId, kind, "step-1", 1) with
        {
            PureNodeOutcomeJson = outcomeJson,
            TraceReservationUtf8Bytes = kind == CustomLoopRunEventKind.NodeAttemptStarted
                ? CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes
                : null,
        };
        return WithEvidence(
            runEvent,
            binding,
            "step-1",
            1,
            kind == CustomLoopRunEventKind.NodeAttemptStarted
                ? CustomLoopSequentialNodeEvidenceKind.DispatchStarted
                : CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            kind == CustomLoopRunEventKind.NodeAttemptStarted
                ? CustomLoopSequentialNodeDisposition.Unknown
                : CustomLoopSequentialNodeDisposition.Completed);
    }

    private static GovernedLoopSequentialOrderedNodeEvidenceRequest OrderedRequest(
        SequentialContext context,
        GovernedLoopSequentialPlanNode node,
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialNodeHandlerResultStatus disposition = GovernedLoopSequentialNodeHandlerResultStatus.Completed,
        int? orderedLifecycleVersion = null)
    {
        var evidence = Assert.IsType<CustomLoopSequentialNodeEvidence>(runEvent.SequentialNodeEvidence);
        var activation = GovernedLoopNodeExecutionEvidence.CreateActivation(
            evidence.ActivationOrdinal,
            node.Ordinal,
            evidence.VisitOrdinal,
            node.NodeId,
            node.Descriptor,
            node.IncomingControlEdgeIds,
            node.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Running,
            evidence.Attempt,
            $"ordered-{node.NodeId}",
            cycleId: evidence.CycleId,
            cycleIteration: evidence.CycleIteration);
        return new GovernedLoopSequentialOrderedNodeEvidenceRequest(
            1,
            new GovernedLoopSequentialNodeDispatchRequest(1, context.Anchor, context.Plan, node, activation, evidence.Attempt!.Value),
            disposition,
            orderedLifecycleVersion ?? context.Run.LifecycleVersion,
            runEvent.Sequence,
            runEvent.EventId);
    }

    private static Process StartCrossProcessResolver(string workspaceRoot, string evidenceHash, string resultPath)
        => CancellationHostProcess.Start(
            "sequential-evidence-resolve",
            workspaceRoot,
            evidenceHash,
            resultPath);

    private static JsonObject ReverseProperties(JsonObject value)
    {
        var reordered = new JsonObject();
        foreach (var property in value.Reverse())
        {
            reordered[property.Key] = property.Value?.DeepClone();
        }

        return reordered;
    }

    private static void AssertCanonicalOrderRejected(JsonObject root)
    {
        var exception = Assert.Throws<FormatException>(() => CustomLoopRunArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")));
        Assert.Contains("not in canonical serializer order", exception.Message, StringComparison.Ordinal);
    }

    private static string Hash(char value) => new(value, 64);

    private sealed record WaitRunStages(
        CustomLoopRunRecord Admitted,
        CustomLoopRunRecord InferenceRunning,
        CustomLoopRunRecord ReadyForWait,
        CustomLoopRunRecord WaitRunning,
        CustomLoopRunRecord Waiting,
        CustomLoopRunRecord Checkpointed,
        CustomLoopRunRecord SkippedParkPhase,
        CustomLoopRunRecord Continued,
        CustomLoopRunRecord Completed);

    private sealed record HumanInputRunStages(
        CustomLoopRunRecord Admitted,
        CustomLoopRunRecord InferenceRunning,
        CustomLoopRunRecord HumanInputReady,
        CustomLoopRunRecord HumanInputRunning,
        CustomLoopRunRecord Waiting,
        GovernedLoopHumanInputNodeConfiguration Configuration);

    private sealed record RetryCapacityStages(
        CustomLoopRunStore Store,
        SequentialContext Context,
        GovernedLoopNodeExecutionEvidence Activation,
        CustomLoopRunRecord ScheduledRun,
        CustomLoopRunRecord Checkpointed,
        GovernedLoopRetryState Attached,
        DateTimeOffset EligibleAtUtc,
        CustomLoopRunStoreStatus LatestStoreStatus);

    internal sealed record SequentialContext(
        CustomLoopRunRecord Run,
        GovernedLoopSequentialInvocationSnapshot Invocation,
        GovernedLoopSequentialAdapterBinding Binding,
        GovernedLoopSequentialRunAnchor Anchor,
        GovernedLoopSequentialPlan Plan);
}
