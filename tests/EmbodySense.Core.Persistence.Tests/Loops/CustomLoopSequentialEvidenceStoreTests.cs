using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
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
            CustomLoopSequentialNodeDisposition.Rejected);
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

    internal static SequentialContext CreateContext(GovernedLoopSequentialTriggerOrigin? triggerOrigin = null, string identity = "sequential", bool scheduleTrigger = false)
        => CreateContext(LinearGraph(scheduleTrigger), triggerOrigin, identity, scheduleTrigger);

    private static SequentialContext CreateContext(
        GovernedLoopGraphDefinition graph,
        GovernedLoopSequentialTriggerOrigin? triggerOrigin = null,
        string identity = "sequential",
        bool scheduleTrigger = false)
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
        var intent = new GovernedLoopAdmissionIntent(
            1,
            GovernedLoopAdmissionTestFixture.WorkspaceId,
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
        var capabilityAdmission = SequentialCapabilityAdmission(artifact.ArtifactHash, scheduleTrigger);
        var effectiveAuthority = GovernedLoopAdmissionTestFixture.EffectiveAuthority();
        var grantBoundary = new AuthorityGrantBoundary(_timestamp.AddMinutes(-1), _timestamp.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var grantDependencyEvidenceHash = Hash('9');
        var evaluatedAtUtc = _timestamp.AddMinutes(1);
        var modelRoutingAdmission = ModelRoutingAdmission(
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

        var definition = CustomLoopDefinition.CreateSeed("sequential-loop", "default-role", "step-1", "create-loop", _timestamp);
        var admitted = WithEvidence(
            Event(1, "event-admitted", CustomLoopRunEventKind.Admitted),
            binding,
            "trigger",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
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
        CustomLoopSequentialNodeDisposition disposition)
    {
        var activationOrdinal = string.Equals(nodeId, "trigger", StringComparison.Ordinal)
            ? 0
            : string.Equals(nodeId, "exit", StringComparison.Ordinal) ? 2 : 1;
        var controlOutcome = kind switch
        {
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted => (GovernedLoopControlCondition?)null,
            _ when string.Equals(nodeId, "trigger", StringComparison.Ordinal) => GovernedLoopControlCondition.Always,
            _ when disposition == CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopControlCondition.Failure,
            _ => GovernedLoopControlCondition.Success,
        };
        var outgoing = nodeId switch
        {
            "trigger" => new[] { "trigger-to-step" },
            "step-1" => new[] { "step-to-exit" },
            _ => [],
        };
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
            controlOutcome is null or GovernedLoopControlCondition.Failure ? [] : outgoing,
            controlOutcome == GovernedLoopControlCondition.Failure ? outgoing : [],
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
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
        CustomLoopRunEvent runEvent)
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
            GovernedLoopSequentialNodeHandlerResultStatus.Completed,
            context.Run.LifecycleVersion,
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

    internal sealed record SequentialContext(
        CustomLoopRunRecord Run,
        GovernedLoopSequentialInvocationSnapshot Invocation,
        GovernedLoopSequentialAdapterBinding Binding,
        GovernedLoopSequentialRunAnchor Anchor,
        GovernedLoopSequentialPlan Plan);
}
