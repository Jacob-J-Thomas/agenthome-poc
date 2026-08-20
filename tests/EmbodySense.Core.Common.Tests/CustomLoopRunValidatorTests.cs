using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Tests.Loops.Admission;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Common.Tests;

public sealed class CustomLoopRunValidatorTests
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    private const string WorkspaceReadCapabilityId = "org.embodysense/workspace-read";

    private static readonly DateTimeOffset _timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");

    [Fact]
    public void Same_durable_version_requires_every_valid_field_event_and_frontier_hash_to_match()
    {
        var run = CreateSequentialRun();
        var exactCopy = run with
        {
            Events = run.Events.Select(item => item with { ContextBlocks = [.. item.ContextBlocks] }).ToArray(),
        };
        var substitutedEvent = run with
        {
            Events = [run.Events[0] with { Detail = "Substituted same-version evidence." }],
        };
        var substitutedFrontier = WithPureFrontier(run, "transform-1");

        Assert.True(CustomLoopRunValidator.HasSameDurableVersion(run, exactCopy));
        Assert.False(CustomLoopRunValidator.HasSameDurableVersion(run, substitutedEvent));
        Assert.True(CustomLoopRunValidator.Validate(substitutedFrontier).IsValid);
        Assert.False(CustomLoopRunValidator.HasSameDurableVersion(run, substitutedFrontier));
        Assert.False(CustomLoopRunValidator.HasSameDurableVersion(run, run with { UpdatedAtUtc = run.UpdatedAtUtc.AddTicks(1) }));
    }

    [Fact]
    public void Exact_durable_event_prefix_accepts_an_unchanged_record_and_a_valid_later_successor()
    {
        var prefix = CreateSequentialRun();
        var later = CreateRunningSequentialRun(prefix);

        Assert.True(CustomLoopRunValidator.Validate(prefix).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(prefix).Errors));
        Assert.True(CustomLoopRunValidator.Validate(later).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(later).Errors));
        Assert.True(CustomLoopRunValidator.HasExactDurableEventPrefix(prefix, prefix));
        Assert.True(CustomLoopRunValidator.HasExactDurableEventPrefix(prefix, later));
    }

    [Fact]
    public void Exact_durable_event_prefix_rejects_substitution_regression_invalid_shapes_and_admission_drift()
    {
        var prefix = CreateSequentialRun();
        var later = CreateRunningSequentialRun(prefix);
        var substitutedTrigger = WithSequentialEvidence(
            later.Events[0] with { Detail = "A valid later record substituted its durable trigger prefix." },
            later.SequentialAdapterBinding!,
            "trigger-node",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var substituted = later with
        {
            Events = [substitutedTrigger, .. later.Events.Skip(1)],
            Frontier = ReplaceTriggerOutcome(
                later.Frontier!,
                substitutedTrigger.EventId,
                substitutedTrigger.SequentialNodeEvidence!.OutcomeArtifactHash),
        };
        var invalidPrefix = prefix with { Events = [] };
        var divergentSameVersionPrefix = prefix with
        {
            LifecycleVersion = later.LifecycleVersion,
            UpdatedAtUtc = later.UpdatedAtUtc,
        };
        var originalAdmission = CreateRun();
        var driftedAdmission = CreateRun(loopId: "loop-beta");

        Assert.True(CustomLoopRunValidator.Validate(substituted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(substituted).Errors));
        Assert.True(CustomLoopRunValidator.Validate(divergentSameVersionPrefix).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(divergentSameVersionPrefix).Errors));
        Assert.True(CustomLoopRunValidator.Validate(originalAdmission).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(originalAdmission).Errors));
        Assert.True(CustomLoopRunValidator.Validate(driftedAdmission).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(driftedAdmission).Errors));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(prefix, substituted));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(later, prefix));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(divergentSameVersionPrefix, later));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(invalidPrefix, later));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(originalAdmission, driftedAdmission));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(null, later));
        Assert.False(CustomLoopRunValidator.HasExactDurableEventPrefix(prefix, null));
    }

    [Fact]
    public void Sequential_trigger_evidence_is_payload_bound_and_required_to_match_exact_run_coordinates()
    {
        var run = CreateSequentialRun();

        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        Assert.NotNull(run.Events[0].SequentialNodeEvidence);
        Assert.True(CustomLoopSequentialOutcomeArtifactHash.Matches(run.Events[0]));
        Assert.True(CustomLoopSequentialNodeEvidenceHash.Matches(run.Events[0].SequentialNodeEvidence));

        var substitutedPayload = run with
        {
            Events = [run.Events[0] with { Detail = "Substituted trigger evidence." }],
        };
        AssertCodes(CustomLoopRunValidator.Validate(substitutedPayload), "invalid_sequential_node_evidence");

        var substitutedRun = run with
        {
            Events = [run.Events[0] with
            {
                SequentialNodeEvidence = run.Events[0].SequentialNodeEvidence! with { RunId = "run-other" },
            }],
        };
        AssertCodes(CustomLoopRunValidator.Validate(substitutedRun), "invalid_sequential_node_evidence");
    }

    [Fact]
    public void Sequential_capability_admission_binds_the_graph_artifact_and_exact_sorted_roots()
    {
        var run = CreateSequentialRun();

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.NotEqual(GetRequirementsHash(run.AdmittedDefinition), run.CapabilityAdmission.RequirementsHash);
        Assert.Equal("sha256:" + run.SequentialAdapterBinding!.GraphArtifactHash, run.CapabilityAdmission.Requirements.Artifact.Checksum?.Value);
        Assert.Equal(
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId],
            run.CapabilityAdmission.Evidence
                .Where(item => item.SubjectId.Equals(run.CapabilityAdmission.Requirements.SubjectId) && item.Outcome == "Selected")
                .Select(item => item.SelectedIdentity!.Id.Value));

        var legacy = CreateRun();
        var sequentialAdmissionOnLegacyRun = CustomLoopAdmissionRequestHash.Apply(legacy with { CapabilityAdmission = run.CapabilityAdmission });
        AssertCodes(CustomLoopRunValidator.Validate(sequentialAdmissionOnLegacyRun), "capability_admission_definition_mismatch");
    }

    [Fact]
    public void Sequential_tool_enabled_capability_admission_requires_exact_catalog_assignments_and_roots()
    {
        var run = CreateSequentialRun();
        var assignments = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
        var enabled = WithSequentialToolAssignments(
            run,
            assignments,
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceCommandCapabilityId]);
        var missing = WithSequentialToolAssignments(
            run,
            assignments,
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId]);
        var extra = WithSequentialToolAssignments(
            run,
            assignments,
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceCommandCapabilityId, WorkspaceReadCapabilityId]);
        var substituted = WithSequentialToolAssignments(
            run,
            assignments,
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceReadCapabilityId]);
        var partialAssignments = WithSequentialToolAssignments(
            run,
            [CustomLoopToolAssignment.Read],
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceCommandCapabilityId]);

        Assert.True(CustomLoopRunValidator.Validate(enabled).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(enabled).Errors));
        Assert.Equal(
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceCommandCapabilityId],
            enabled.CapabilityAdmission.Evidence
                .Where(item => item.SubjectId.Equals(enabled.CapabilityAdmission.Requirements.SubjectId) && item.Outcome == "Selected")
                .Select(item => item.SelectedIdentity!.Id.Value));
        AssertCodes(CustomLoopRunValidator.Validate(missing), "sequential_capability_identity_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(extra), "sequential_capability_identity_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(substituted), "sequential_capability_identity_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(partialAssignments), "sequential_tool_assignment_mismatch");
    }

    [Fact]
    public void Sequential_capability_admission_rejects_self_consistent_graph_or_root_identity_substitution()
    {
        var run = CreateSequentialRun();
        var substitutedGraph = CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = CreateSequentialCapabilityAdmission(run.SequentialAdapterBinding!, [ConversationTurnCapabilityId, ModelInferenceCapabilityId], new string('9', 64)),
        });
        var rootSubstitution = CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = CreateSequentialCapabilityAdmission(run.SequentialAdapterBinding!, [ModelInferenceCapabilityId, WorkspaceCommandCapabilityId]),
        });

        Assert.Null(CapabilityAdmissionSnapshotValidator.Validate(substitutedGraph.CapabilityAdmission));
        Assert.Null(CapabilityAdmissionSnapshotValidator.Validate(rootSubstitution.CapabilityAdmission));
        AssertCodes(CustomLoopRunValidator.Validate(substitutedGraph), "sequential_capability_graph_artifact_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(rootSubstitution), "sequential_capability_identity_mismatch");
    }

    [Fact]
    public void Sequential_capability_admission_rejects_missing_extra_and_duplicate_selected_roots()
    {
        var run = CreateSequentialRun();
        var missing = CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = CreateSequentialCapabilityAdmission(run.SequentialAdapterBinding!, [ModelInferenceCapabilityId]),
        });
        var extra = CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = CreateSequentialCapabilityAdmission(run.SequentialAdapterBinding!, [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceCommandCapabilityId]),
        });
        var duplicateAdmission = run.CapabilityAdmission with
        {
            Evidence = [.. run.CapabilityAdmission.Evidence, run.CapabilityAdmission.Evidence[0]],
        };
        var duplicate = CustomLoopAdmissionRequestHash.Apply(run with { CapabilityAdmission = duplicateAdmission });

        AssertCodes(CustomLoopRunValidator.Validate(missing), "sequential_capability_identity_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(extra), "sequential_capability_identity_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(duplicate), "invalid_capability_admission");
    }

    [Fact]
    public void Sequential_capability_admission_rejects_malformed_hash_and_resolution_evidence_before_specific_binding_checks()
    {
        var run = CreateSequentialRun();
        var forgedHash = CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = run.CapabilityAdmission with { RequirementsHash = "sha256:" + new string('9', 64) },
        });
        var missingRootEvidence = CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = run.CapabilityAdmission with { Evidence = [] },
        });

        AssertCodes(CustomLoopRunValidator.Validate(forgedHash), "invalid_capability_admission");
        AssertCodes(CustomLoopRunValidator.Validate(missingRootEvidence), "invalid_capability_admission");
    }

    [Fact]
    public void Sequential_inference_evidence_requires_the_exact_admitted_legacy_step_identity()
    {
        var run = CreateSequentialRun();
        var binding = run.SequentialAdapterBinding!;
        var validStart = SequentialEvent(2, "inference-start", CustomLoopRunEventKind.NodeAttemptStarted, binding, "step-1", "step-1", CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var validOutcome = SequentialEvent(3, "inference-complete", CustomLoopRunEventKind.NodeAttemptCompleted, binding, "step-1", "step-1", CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed);
        var valid = WithAttemptEvidence(run, validStart, validOutcome, GovernedLoopNodeExecutionStatus.Completed);
        Assert.True(CustomLoopRunValidator.Validate(valid).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(valid).Errors));

        var mismatchedStart = SequentialEvent(2, "inference-start", CustomLoopRunEventKind.NodeAttemptStarted, binding, "canonical-inference", "step-1", CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var mismatchedOutcome = SequentialEvent(3, "inference-complete", CustomLoopRunEventKind.NodeAttemptCompleted, binding, "canonical-inference", "step-1", CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed);
        var unknownStart = SequentialEvent(2, "inference-start", CustomLoopRunEventKind.NodeAttemptStarted, binding, "other-step", "other-step", CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var unknownOutcome = SequentialEvent(3, "inference-complete", CustomLoopRunEventKind.NodeAttemptCompleted, binding, "other-step", "other-step", CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed);

        AssertCodes(CustomLoopRunValidator.Validate(WithAttemptEvidence(run, mismatchedStart, mismatchedOutcome, GovernedLoopNodeExecutionStatus.Completed)), "sequential_inference_step_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(WithAttemptEvidence(run, unknownStart, unknownOutcome, GovernedLoopNodeExecutionStatus.Completed)), "sequential_inference_step_mismatch");
    }

    [Fact]
    public void Pure_node_outcomes_are_bounded_hash_bound_and_coupled_to_exact_frontier_nodes()
    {
        var run = WithPureFrontier(CreateSequentialRun(), "transform-1");
        var start = PureSequentialEvent(2, "pure-start", CustomLoopRunEventKind.NodeAttemptStarted, run.SequentialAdapterBinding!, "transform-1");
        var completion = PureSequentialEvent(3, "pure-complete", CustomLoopRunEventKind.NodeAttemptCompleted, run.SequentialAdapterBinding!, "transform-1", "{\"schemaVersion\":1}");
        var valid = WithAttemptEvidence(run, start, completion, GovernedLoopNodeExecutionStatus.Completed);

        Assert.True(CustomLoopRunValidator.Validate(valid).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(valid).Errors));
        Assert.Equal(CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes, start.TraceReservationUtf8Bytes);
        Assert.True(CustomLoopSequentialOutcomeArtifactHash.Matches(completion));

        var tampered = valid with { Events = [run.Events[0], start, completion with { PureNodeOutcomeJson = "{}" }] };
        AssertCodes(CustomLoopRunValidator.Validate(tampered), "invalid_sequential_node_evidence");

        var oversized = PureSequentialEvent(
            3,
            "pure-oversized",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            run.SequentialAdapterBinding!,
            "transform-1",
            new string('x', CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes + 1));
        AssertCodes(CustomLoopRunValidator.Validate(WithAttemptEvidence(run, start, oversized, GovernedLoopNodeExecutionStatus.Completed)), "pure_node_outcome_too_large");

        var ambientProvider = PureSequentialEvent(
            3,
            "pure-provider",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            run.SequentialAdapterBinding!,
            "transform-1",
            "{}",
            provider: "forbidden-provider");
        AssertCodes(CustomLoopRunValidator.Validate(WithAttemptEvidence(run, start, ambientProvider, GovernedLoopNodeExecutionStatus.Completed)), "invalid_pure_node_outcome_payload");
    }

    [Fact]
    public void Pure_node_attempt_coordinates_require_the_exact_reservation_or_prior_pure_dispatch()
    {
        var run = WithPureFrontier(CreateSequentialRun(), "validate-1", GovernedLoopNodeKind.Validate);
        var validStart = PureSequentialEvent(2, "pure-start", CustomLoopRunEventKind.NodeAttemptStarted, run.SequentialAdapterBinding!, "validate-1");
        var validFailure = PureSequentialEvent(
            3,
            "pure-failed",
            CustomLoopRunEventKind.NodeAttemptFailed,
            run.SequentialAdapterBinding!,
            "validate-1",
            evidenceKind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            disposition: CustomLoopSequentialNodeDisposition.Rejected);
        var invalidStart = PureSequentialEvent(
            2,
            "pure-start-invalid",
            CustomLoopRunEventKind.NodeAttemptStarted,
            run.SequentialAdapterBinding!,
            "validate-1",
            reservation: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);

        Assert.True(CustomLoopRunValidator.Validate(WithAttemptEvidence(run, validStart, validFailure, GovernedLoopNodeExecutionStatus.Failed)).IsValid);
        AssertCodes(
            CustomLoopRunValidator.Validate(WithAttemptEvidence(run, invalidStart, validFailure, GovernedLoopNodeExecutionStatus.Failed)),
            "sequential_pure_node_step_mismatch",
            "attempt_trace_reservation_required");
    }

    [Fact]
    public void Sequential_checkpoints_may_retain_only_exact_completed_pure_frontier_outputs()
    {
        var run = WithPureFrontier(CreateSequentialRun(), "transform-1");
        var start = PureSequentialEvent(2, "pure-start", CustomLoopRunEventKind.NodeAttemptStarted, run.SequentialAdapterBinding!, "transform-1");
        var completion = PureSequentialEvent(3, "pure-complete", CustomLoopRunEventKind.NodeAttemptCompleted, run.SequentialAdapterBinding!, "transform-1", "{}");
        var retained = new CustomLoopRetainedOutput("transform-1", 1, "transformed", CustomLoopTraceContentHash.Compute("transformed"));
        var checkpoint = run.Checkpoint with { CurrentIterationResult = retained };
        var valid = WithAttemptEvidence(run, start, completion, GovernedLoopNodeExecutionStatus.Completed) with { Checkpoint = checkpoint };
        var missingOutcome = run with { Checkpoint = checkpoint };
        var legacy = CreateRun() with { Checkpoint = CreateRun().Checkpoint with { CurrentIterationResult = retained } };

        Assert.True(CustomLoopRunValidator.Validate(valid).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(valid).Errors));
        AssertCodes(CustomLoopRunValidator.Validate(missingOutcome), "unknown_retained_step");
        AssertCodes(CustomLoopRunValidator.Validate(legacy), "unknown_retained_step");
    }

    [Fact]
    public void Sequential_exit_evidence_requires_the_reserved_legacy_exit_step_without_aliasing_the_canonical_node()
    {
        var run = CreateSequentialRun();
        var binding = run.SequentialAdapterBinding!;
        var validStart = SequentialEvent(2, "exit-start", CustomLoopRunEventKind.ExitDecisionStarted, binding, "canonical-exit-node", "exit", CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var validOutcome = SequentialEvent(3, "exit-complete", CustomLoopRunEventKind.ExitDecisionCompleted, binding, "canonical-exit-node", "exit", CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed);
        var valid = WithExitAttemptEvidence(run, validStart, validOutcome, GovernedLoopNodeExecutionStatus.Completed);
        Assert.True(CustomLoopRunValidator.Validate(valid).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(valid).Errors));

        var wrongStart = SequentialEvent(2, "exit-start", CustomLoopRunEventKind.ExitDecisionStarted, binding, "canonical-exit-node", "canonical-exit-node", CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var wrongOutcome = SequentialEvent(3, "exit-complete", CustomLoopRunEventKind.ExitDecisionCompleted, binding, "canonical-exit-node", "canonical-exit-node", CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed);

        AssertCodes(CustomLoopRunValidator.Validate(WithExitAttemptEvidence(run, wrongStart, wrongOutcome, GovernedLoopNodeExecutionStatus.Completed)), "sequential_exit_step_mismatch");
    }

    [Fact]
    public void Sequential_exit_rejection_is_classified_by_its_exact_prior_exit_dispatch()
    {
        var run = CreateSequentialRun();
        var binding = run.SequentialAdapterBinding!;
        var exitStart = SequentialEvent(2, "exit-start", CustomLoopRunEventKind.ExitDecisionStarted, binding, "canonical-exit-node", "exit", CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var exitRejection = SequentialEvent(3, "exit-rejected", CustomLoopRunEventKind.NodeAttemptFailed, binding, "canonical-exit-node", "exit", CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected);
        var valid = WithExitAttemptEvidence(run, exitStart, exitRejection, GovernedLoopNodeExecutionStatus.Failed);
        Assert.True(CustomLoopRunValidator.Validate(valid).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(valid).Errors));

        var substitutedStep = SequentialEvent(3, "exit-rejected", CustomLoopRunEventKind.NodeAttemptFailed, binding, "canonical-exit-node", "canonical-exit-node", CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected);
        var inferenceStart = SequentialEvent(2, "inference-start", CustomLoopRunEventKind.NodeAttemptStarted, binding, "canonical-exit-node", "exit", CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        AssertCodes(CustomLoopRunValidator.Validate(WithExitAttemptEvidence(run, exitStart, substitutedStep, GovernedLoopNodeExecutionStatus.Failed)), "sequential_exit_step_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(WithExitAttemptEvidence(run, inferenceStart, exitRejection, GovernedLoopNodeExecutionStatus.Failed)), "sequential_inference_step_mismatch");
    }

    [Fact]
    public void Sequential_trigger_evidence_forbids_legacy_step_coordinates()
    {
        var run = CreateSequentialRun();
        var coordinatedTrigger = run.Events[0] with { Iteration = 1, StepId = "trigger-node", Attempt = 1 };
        coordinatedTrigger = WithSequentialEvidence(
            coordinatedTrigger,
            run.SequentialAdapterBinding!,
            "trigger-node",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);

        AssertCodes(CustomLoopRunValidator.Validate(run with { Events = [coordinatedTrigger] }), "sequential_trigger_coordinates_mismatch");
    }

    [Fact]
    public void Sequential_initial_materialization_requires_trigger_evidence_without_changing_the_legacy_shape()
    {
        var run = CreateSequentialRun();
        var missingTriggerEvidence = run with
        {
            Events = [run.Events[0] with { SequentialNodeEvidence = null }],
        };

        AssertCodes(CustomLoopRunValidator.Validate(missingTriggerEvidence), "sequential_trigger_evidence_required");
        Assert.True(CustomLoopRunValidator.Validate(CreateRun()).IsValid);
    }

    [Fact]
    public void Sequential_frontier_is_required_exactly_bound_and_excluded_from_the_admission_hash()
    {
        var run = CreateSequentialRun();
        var withoutFrontier = run with { Frontier = null };
        var legacyWithFrontier = CreateRun() with { Frontier = run.Frontier };
        var source = run.Frontier!;
        var unhashedPayload = GovernedLoopFrontierPayload.Create(
            source.Payload.SchemaVersion,
            source.Payload.FrontierVersion,
            source.Payload.ConcurrencyCeiling,
            source.Payload.Status,
            source.Payload.Nodes,
            source.Payload.UpdatedAtUtc,
            string.Empty);
        var substitutedWorkspace = run with
        {
            Frontier = GovernedLoopFrontierPosture.Create(
                source.Binding,
                "workspace-sha256:" + new string('9', 64),
                source.GraphArtifactHash,
                source.GraphLayoutHash,
                source.AdmissionReceiptHash,
                unhashedPayload),
        };

        Assert.True(CustomLoopRunValidator.Validate(run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(run).Errors));
        AssertCodes(CustomLoopRunValidator.Validate(withoutFrontier), "execution_frontier_required");
        AssertCodes(CustomLoopRunValidator.Validate(legacyWithFrontier), "execution_frontier_binding_required");
        AssertCodes(CustomLoopRunValidator.Validate(substitutedWorkspace), "execution_frontier_binding_mismatch");
        Assert.Equal(CustomLoopAdmissionRequestHash.Compute(run), CustomLoopAdmissionRequestHash.Compute(withoutFrontier));
    }

    [Fact]
    public void Sequential_frontier_update_requires_stable_presence_or_one_exact_legal_successor()
    {
        var current = CreateSequentialRun();
        var running = CreateRunningSequentialRun(current);
        var unchanged = Advance(current, CustomLoopRunStatus.Running);
        var removed = running with { Frontier = null };
        var skippedPayload = GovernedLoopFrontierPayload.Create(
            1,
            3,
            running.Frontier!.Payload.ConcurrencyCeiling,
            running.Frontier.Payload.Status,
            running.Frontier.Payload.Nodes,
            running.Frontier.Payload.UpdatedAtUtc,
            string.Empty);
        var skippedVersion = running with
        {
            Frontier = GovernedLoopFrontierPosture.Create(
                running.Frontier.Binding,
                running.Frontier.WorkspaceId,
                running.Frontier.GraphArtifactHash,
                running.Frontier.GraphLayoutHash,
                running.Frontier.AdmissionReceiptHash,
                skippedPayload),
        };

        Assert.True(CustomLoopRunValidator.ValidateUpdate(current, running).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(current, running).Errors));
        Assert.True(CustomLoopRunValidator.ValidateUpdate(current, unchanged).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(current, unchanged).Errors));
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, removed), "execution_frontier_required", "execution_frontier_presence_changed");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, skippedVersion), "invalid_execution_frontier_transition");
    }

    [Fact]
    public void Paused_active_frontier_requires_an_exact_authenticated_terminal_for_its_running_attempt()
    {
        var running = CreateRunningSequentialRun(CreateSequentialRun());
        var pausedSeed = Advance(running, CustomLoopRunStatus.Paused);
        var terminal = SequentialEvent(
            running.Events.Length + 1L,
            "inference-complete",
            CustomLoopRunEventKind.NodeAttemptCompleted,
            running.SequentialAdapterBinding!,
            "step-1",
            "step-1",
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            pausedSeed.UpdatedAtUtc);
        var lifecycle = pausedSeed.Events[^1] with
        {
            Sequence = terminal.Sequence + 1,
            EventId = "event-paused-after-retained-terminal",
        };
        var valid = pausedSeed with { Events = [.. running.Events, terminal, lifecycle] };

        Assert.True(CustomLoopRunValidator.Validate(valid).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(valid).Errors));

        var startIndex = Array.FindIndex(valid.Events, item => item.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            NodeId: "step-1",
        });
        var substitutedStart = WithSequentialEvidence(
            valid.Events[startIndex] with { EventId = "substituted-start", SequentialNodeEvidence = null },
            valid.SequentialAdapterBinding!,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var substitutedStartStep = WithSequentialEvidence(
            valid.Events[startIndex] with { StepId = "other-step", SequentialNodeEvidence = null },
            valid.SequentialAdapterBinding!,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var terminalIndex = valid.Events.Length - 2;
        var substitutedIteration = WithSequentialEvidence(
            valid.Events[terminalIndex] with { Iteration = 2, SequentialNodeEvidence = null },
            valid.SequentialAdapterBinding!,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var substitutedStep = WithSequentialEvidence(
            valid.Events[terminalIndex] with { StepId = "other-step", SequentialNodeEvidence = null },
            valid.SequentialAdapterBinding!,
            "step-1",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var substitutedAttempt = WithSequentialEvidence(
            valid.Events[terminalIndex] with { Attempt = 2, SequentialNodeEvidence = null },
            valid.SequentialAdapterBinding!,
            "step-1",
            2,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var tamperedTerminal = valid.Events[terminalIndex] with { Detail = "tampered without rehash" };

        foreach (var malformed in new[]
        {
            valid with { Events = ReplaceEvent(valid.Events, startIndex, substitutedStart) },
            valid with
            {
                Events = ReplaceEvent(
                    ReplaceEvent(valid.Events, startIndex, substitutedStartStep),
                    terminalIndex,
                    substitutedStep),
            },
            valid with { Events = ReplaceEvent(valid.Events, terminalIndex, substitutedIteration) },
            valid with { Events = ReplaceEvent(valid.Events, terminalIndex, substitutedStep) },
            valid with { Events = ReplaceEvent(valid.Events, terminalIndex, substitutedAttempt) },
            valid with { Events = ReplaceEvent(valid.Events, terminalIndex, tamperedTerminal) },
        })
        {
            AssertCodes(CustomLoopRunValidator.Validate(malformed), "execution_frontier_lifecycle_mismatch");
        }
    }

    [Fact]
    public void Needs_review_frontier_may_retain_each_exact_closed_terminal_disposition()
    {
        var cases = new[]
        {
            (CustomLoopRunEventKind.NodeAttemptCompleted, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed),
            (CustomLoopRunEventKind.NodeAttemptFailed, CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected),
            (CustomLoopRunEventKind.NodeAttemptFailed, CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.NeedsReview),
        };

        foreach (var (eventKind, evidenceKind, disposition) in cases)
        {
            var admitted = CreateSequentialRun();
            var running = CreateRunningSequentialRun(admitted);
            var reviewBaseSeed = Advance(running, CustomLoopRunStatus.NeedsReview);
            var reviewEvidence = SequentialEvent(
                running.Events.Length + 1L,
                $"review-outcome-{(int)evidenceKind}",
                eventKind,
                running.SequentialAdapterBinding!,
                "step-1",
                "step-1",
                evidenceKind,
                disposition,
                reviewBaseSeed.UpdatedAtUtc);
            var reviewLifecycle = reviewBaseSeed.Events[^1] with
            {
                Sequence = reviewEvidence.Sequence + 1,
                EventId = $"event-review-lifecycle-{(int)evidenceKind}",
            };
            var reviewBase = reviewBaseSeed with { Events = [.. running.Events, reviewEvidence, reviewLifecycle] };
            var review = reviewBase with
            {
                Frontier = TransitionInferenceFrontier(running.Frontier!, GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopNodeExecutionStatus.ReviewBlocked, reviewBase.UpdatedAtUtc, reviewEvidence),
            };

            Assert.True(CustomLoopRunValidator.ValidateUpdate(running, review).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(running, review).Errors));
        }
    }

    [Fact]
    public void Needs_review_frontier_rejects_malformed_terminal_kind_disposition_pairs()
    {
        var cases = new[]
        {
            (CustomLoopRunEventKind.NodeAttemptCompleted, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Rejected),
            (CustomLoopRunEventKind.NodeAttemptFailed, CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Completed),
            (CustomLoopRunEventKind.NodeAttemptFailed, CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.Rejected),
        };

        foreach (var (eventKind, evidenceKind, disposition) in cases)
        {
            var admitted = CreateSequentialRun();
            var running = CreateRunningSequentialRun(admitted);
            var reviewBaseSeed = Advance(running, CustomLoopRunStatus.NeedsReview);
            var malformedEvidence = SequentialEvent(
                running.Events.Length + 1L,
                $"malformed-review-{(int)evidenceKind}-{(int)disposition}",
                eventKind,
                running.SequentialAdapterBinding!,
                "step-1",
                "step-1",
                evidenceKind,
                disposition,
                reviewBaseSeed.UpdatedAtUtc);
            var lifecycle = reviewBaseSeed.Events[^1] with
            {
                Sequence = malformedEvidence.Sequence + 1,
                EventId = $"event-malformed-review-{(int)evidenceKind}-{(int)disposition}",
            };
            var reviewBase = reviewBaseSeed with { Events = [.. running.Events, malformedEvidence, lifecycle] };
            var malformed = reviewBase with
            {
                Frontier = TransitionInferenceFrontier(running.Frontier!, GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopNodeExecutionStatus.ReviewBlocked, reviewBase.UpdatedAtUtc, malformedEvidence),
            };

            AssertCodes(CustomLoopRunValidator.Validate(malformed), "invalid_sequential_node_evidence", "execution_frontier_outcome_evidence_mismatch");
        }
    }

    [Fact]
    public void Frontier_outcomes_require_the_exact_retained_event_node_attempt_hash_and_disposition()
    {
        var run = CreateSequentialRun();
        var trigger = run.Frontier!.Payload.Nodes[0];
        var missing = run with { Frontier = ReplaceTriggerOutcome(run.Frontier, "missing-event", trigger.OutcomeEvidenceHash!) };
        var mismatchedHash = run with { Frontier = ReplaceTriggerOutcome(run.Frontier, trigger.OutcomeEvidenceId!, new string('8', 64)) };
        var wrongNodeEvent = WithSequentialEvidence(
            run.Events[0] with { SequentialNodeEvidence = null },
            run.SequentialAdapterBinding!,
            "other-trigger",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var wrongAttemptEvent = WithSequentialEvidence(
            run.Events[0] with { SequentialNodeEvidence = null },
            run.SequentialAdapterBinding!,
            "trigger-node",
            2,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var wrongNode = run with
        {
            Events = [wrongNodeEvent],
            Frontier = CreateInitialFrontier(run.SequentialAdapterBinding!, wrongNodeEvent),
        };
        var wrongAttempt = run with
        {
            Events = [wrongAttemptEvent],
            Frontier = CreateInitialFrontier(run.SequentialAdapterBinding!, wrongAttemptEvent),
        };
        var nullEvents = run with { Events = null! };

        AssertCodes(CustomLoopRunValidator.Validate(missing), "execution_frontier_outcome_evidence_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(mismatchedHash), "execution_frontier_outcome_evidence_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(wrongNode), "execution_frontier_outcome_evidence_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(wrongAttempt), "execution_frontier_outcome_evidence_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(nullEvents), "execution_frontier_outcome_evidence_mismatch", "events_required");
    }

    [Fact]
    public void Skipped_frontier_node_references_the_exact_completed_governing_control_outcome()
    {
        var skipped = CreateSkippedRun(CreateSequentialRun());
        var trigger = skipped.Frontier!.Payload.Nodes[0];
        var disconnectedTrigger = GovernedLoopNodeExecutionEvidence.Create(
            trigger.PlanOrdinal,
            trigger.NodeId,
            trigger.Descriptor,
            trigger.IncomingControlEdgeIds,
            [],
            trigger.Status,
            trigger.Attempt,
            trigger.AttemptOperationId,
            trigger.OutcomeEvidenceId,
            trigger.OutcomeEvidenceHash);
        var disconnectedPayload = GovernedLoopFrontierPayload.Create(
            1,
            skipped.Frontier.Payload.FrontierVersion,
            skipped.Frontier.Payload.ConcurrencyCeiling,
            skipped.Frontier.Payload.Status,
            [disconnectedTrigger, .. skipped.Frontier.Payload.Nodes.Skip(1)],
            skipped.Frontier.Payload.UpdatedAtUtc,
            string.Empty);
        var disconnected = skipped with
        {
            Frontier = GovernedLoopFrontierPosture.Create(
                skipped.Frontier.Binding,
                skipped.Frontier.WorkspaceId,
                skipped.Frontier.GraphArtifactHash,
                skipped.Frontier.GraphLayoutHash,
                skipped.Frontier.AdmissionReceiptHash,
                disconnectedPayload),
        };

        Assert.True(CustomLoopRunValidator.Validate(skipped).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(skipped).Errors));
        AssertCodes(CustomLoopRunValidator.Validate(disconnected), "execution_frontier_outcome_evidence_mismatch");
    }

    [Fact]
    public void Validate_accepts_a_complete_admitted_trace_and_hashes_exact_content()
    {
        var run = CreateRun();

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", CustomLoopTraceContentHash.Compute("hello"));
        Assert.True(CustomLoopTraceContentHash.Matches("hello", CustomLoopTraceContentHash.Compute("hello")));
        Assert.False(CustomLoopTraceContentHash.Matches("other", CustomLoopTraceContentHash.Compute("hello")));
        Assert.True(CustomLoopAdmissionRequestHash.Matches(run));
        Assert.NotEqual(run.AdmissionRequestHash, CustomLoopAdmissionRequestHash.Compute(run with { ModelSnapshot = new CustomLoopModelSnapshot("local", "model") }));
        Assert.NotEqual(run.AdmissionRequestHash, CustomLoopAdmissionRequestHash.Compute(run with { AdmissionActor = "embodysense.cli" }));
    }

    [Fact]
    public void Admission_audit_marker_is_required_for_dispatch_and_is_strictly_shaped()
    {
        var pending = CreateRun();
        AssertCodes(CustomLoopRunValidator.ValidateForDispatch(pending), "admission_audit_incomplete");

        var marker = Event(2, "event-audit-complete", CustomLoopRunEventKind.AdmissionAuditCompleted);
        var complete = pending with { LifecycleVersion = 2, Events = [.. pending.Events, marker] };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(pending, complete).IsValid);
        Assert.True(CustomLoopRunValidator.ValidateForDispatch(complete).IsValid);
        Assert.True(CustomLoopRunValidator.HasCompleteAdmissionAudit(complete));

        var duplicate = complete with { LifecycleVersion = 3, Events = [.. complete.Events, Event(3, "event-audit-duplicate", CustomLoopRunEventKind.AdmissionAuditCompleted)] };
        AssertCodes(CustomLoopRunValidator.Validate(duplicate), "duplicate_admission_audit_marker");
        var secretBearing = complete with { Events = [complete.Events[0], marker with { Provider = "must-not-be-here" }] };
        AssertCodes(CustomLoopRunValidator.Validate(secretBearing), "invalid_admission_audit_marker");

        var duplicateAdmissionBeforeAudit = complete with
        {
            LifecycleVersion = 3,
            Events =
            [
                complete.Events[0],
                Event(2, "event-admitted-duplicate-before-audit", CustomLoopRunEventKind.Admitted),
                marker with { Sequence = 3 }
            ]
        };
        AssertCodes(CustomLoopRunValidator.ValidateForDispatch(duplicateAdmissionBeforeAudit), "duplicate_admission_event", "misordered_admission_audit_marker", "admission_audit_incomplete");
        Assert.False(CustomLoopRunValidator.HasCompleteAdmissionAudit(duplicateAdmissionBeforeAudit));

        var duplicateAdmissionAfterAudit = complete with
        {
            LifecycleVersion = 3,
            Events = [.. complete.Events, Event(3, "event-admitted-duplicate-after-audit", CustomLoopRunEventKind.Admitted)]
        };
        AssertCodes(CustomLoopRunValidator.ValidateForDispatch(duplicateAdmissionAfterAudit), "duplicate_admission_event", "admission_audit_incomplete");
        Assert.False(CustomLoopRunValidator.HasCompleteAdmissionAudit(duplicateAdmissionAfterAudit));
    }

    [Fact]
    public void Validate_requires_pinned_model_admission_hash_and_consistent_execution_clock()
    {
        var seed = CreateRun();
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ModelSnapshot = null! }), "model_snapshot_required", "admission_request_hash_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { AdmissionRequestHash = new string('0', 64) }), "admission_request_hash_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ExecutionClock = null! }), "execution_clock_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ExecutionClock = new CustomLoopExecutionClock(-1, _timestamp) }), "execution_clock_out_of_range", "unexpected_active_execution_clock");
        var running = Advance(seed, CustomLoopRunStatus.Running) with { ExecutionClock = CustomLoopExecutionClock.NotStarted() };
        AssertCodes(CustomLoopRunValidator.Validate(running), "active_execution_clock_required");
    }

    [Fact]
    public void Validate_rejects_unsupported_schema_with_pre_1_0_cleanup_guidance()
    {
        var validation = CustomLoopRunValidator.Validate(CreateRun() with { SchemaVersion = 99 });

        var error = Assert.Single(validation.Errors, error => error.Code == "unsupported_run_schema");
        Assert.Contains("Pre-1.0 artifacts from another schema are unsupported", error.Message, StringComparison.Ordinal);
        Assert.Contains("remove and recreate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_control_characters_in_the_persisted_admission_actor()
    {
        var unsafeActor = CustomLoopAdmissionRequestHash.Apply(CreateRun() with { AdmissionActor = "embodysense.web\ninjected" });

        AssertCodes(CustomLoopRunValidator.Validate(unsafeActor), "unsafe_text");
    }

    [Fact]
    public void Validate_requires_exact_output_and_conversation_publication_metadata()
    {
        var seed = CreateRun();
        var missingOutputMetadata = seed.Events[0] with { CanonicalOutput = "output" };
        var inconsistentOutputMetadata = missingOutputMetadata with { OriginalOutputCharacterCount = 3, CanonicalOutputTruncated = false };
        var unexpectedOutputMetadata = seed.Events[0] with { OriginalOutputCharacterCount = 10, CanonicalOutputTruncated = true };
        var missingPublication = seed.Events[0] with { PublishedToInvokingConversation = true };
        var unexpectedPublication = seed.Events[0] with { PublishedToInvokingConversation = false, ConversationPublicationId = "publish-1" };

        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [missingOutputMetadata] }), "output_metadata_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [inconsistentOutputMetadata] }), "inconsistent_output_metadata");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [unexpectedOutputMetadata] }), "unexpected_output_metadata");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [missingPublication] }), "conversation_publication_id_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [unexpectedPublication] }), "unexpected_conversation_publication_id");
    }

    [Fact]
    public void Control_lifecycle_ownership_is_lifecycle_only_unique_and_bound_to_the_update_source_version()
    {
        var seed = CreateRun();
        var unexpected = seed with { Events = [seed.Events[0] with { ControlExpectedLifecycleVersion = 1 }] };
        AssertCodes(CustomLoopRunValidator.Validate(unexpected), "unexpected_control_lifecycle_version");

        var valid = Advance(seed, CustomLoopRunStatus.Running);
        valid = valid with { Events = [.. valid.Events[..^1], valid.Events[^1] with { ControlExpectedLifecycleVersion = seed.LifecycleVersion }] };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(seed, valid).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(seed, valid).Errors));
        var invalidVersion = valid with { Events = [.. valid.Events[..^1], valid.Events[^1] with { ControlExpectedLifecycleVersion = valid.LifecycleVersion }] };
        AssertCodes(CustomLoopRunValidator.Validate(invalidVersion), "invalid_control_lifecycle_version");

        var duplicate = Advance(valid, CustomLoopRunStatus.Paused);
        duplicate = duplicate with { Events = [.. duplicate.Events[..^1], duplicate.Events[^1] with { ControlExpectedLifecycleVersion = seed.LifecycleVersion }] };
        AssertCodes(CustomLoopRunValidator.Validate(duplicate), "duplicate_control_lifecycle_version");

        var audited = seed with { LifecycleVersion = 2, Events = [.. seed.Events, Event(2, "event-audit-complete", CustomLoopRunEventKind.AdmissionAuditCompleted)] };
        var mismatched = Advance(audited, CustomLoopRunStatus.Running);
        mismatched = mismatched with { Events = [.. mismatched.Events[..^1], mismatched.Events[^1] with { ControlExpectedLifecycleVersion = 1 }] };
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(audited, mismatched), "control_lifecycle_version_mismatch");
    }

    [Fact]
    public void Evidence_hashes_and_validation_preserve_exact_non_normalized_unicode()
    {
        const string Decomposed = "e\u0301";
        const string Composed = "é";
        var seed = CreateRun();
        var decomposedSource = WithContent(seed.ContextSnapshot.SourceManifest[0], Decomposed);
        var snapshot = CustomLoopContextSnapshotHash.Apply(seed.ContextSnapshot with { SourceManifest = [decomposedSource, .. seed.ContextSnapshot.SourceManifest.Skip(1)] });
        var run = CustomLoopAdmissionRequestHash.Apply(seed with { ContextSnapshot = snapshot });
        var observed = new CustomLoopRunEvent(2, "event-2", _timestamp, CustomLoopRunEventKind.NodeOutcomeObserved, 1, "step-1", 1, "Observed", [], Decomposed, Decomposed.Length, false, true, false, null, "openai", "gpt-5", "response-1", null);
        run = run with { Events = [.. run.Events, observed] };
        var completed = Advance(Advance(run, CustomLoopRunStatus.Running), CustomLoopRunStatus.Completed) with { FinalOutput = Decomposed };

        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        Assert.True(CustomLoopRunValidator.Validate(completed).IsValid);
        Assert.True(CustomLoopContextSnapshotHash.Matches(snapshot));
        var composedSource = WithContent(snapshot.SourceManifest[0], Composed);
        Assert.NotEqual(CustomLoopContextSnapshotHash.Compute(snapshot), CustomLoopContextSnapshotHash.Compute(snapshot with { SourceManifest = [composedSource, .. snapshot.SourceManifest.Skip(1)] }));
        Assert.NotEqual(CustomLoopTraceContentHash.Compute(Decomposed), CustomLoopTraceContentHash.Compute(Composed));
    }

    [Fact]
    public void Conversation_publication_protocol_requires_iteration_id_and_success_outcome()
    {
        var seed = CreateRun();
        var started = Event(2, "event-2", CustomLoopRunEventKind.ConversationPublicationStarted) with { ConversationPublicationId = "publish-1" };
        var published = Event(2, "event-2", CustomLoopRunEventKind.ConversationPublished, iteration: 1) with { ConversationPublicationId = "publish-1" };

        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [seed.Events[0], started] }), "iteration_coordinate_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [seed.Events[0], published] }), "conversation_publication_outcome_required");
    }

    [Fact]
    public void Validate_rejects_invalid_identity_timestamps_admission_and_terminal_outcomes()
    {
        var seed = CreateRun();
        var invalidDefinition = seed.AdmittedDefinition with { ContentHash = new string('0', 64) };
        var invalid = seed with
        {
            Id = "../escape",
            LoopId = "other-loop",
            LifecycleVersion = 0,
            Status = CustomLoopRunStatus.Unknown,
            CreatedAtUtc = _timestamp.AddMinutes(2).ToOffset(TimeSpan.FromHours(1)),
            UpdatedAtUtc = _timestamp,
            CompletedAtUtc = _timestamp,
            Surface = "Web/UI",
            AdmissionOperationId = "bad operation",
            AdmittedDefinition = invalidDefinition,
            TriggerPrompt = new string('x', CustomLoopLimits.MaxPresetPromptCharacters + 1),
            InvokingConversation = new CustomLoopConversationReference("../conversation", "", _timestamp.AddDays(1))
        };

        var validation = CustomLoopRunValidator.Validate(invalid);

        AssertCodes(validation, "invalid_artifact_id", "invalid_lifecycle_version", "unsupported_run_status", "invalid_surface", "invalid_admission_operation_id", "invalid_created_timestamp", "invalid_timestamp_order", "unexpected_completed_timestamp", "content_hash_mismatch", "admitted_loop_mismatch", "text_too_long", "text_required", "invalid_conversation_capture_timestamp");
    }

    [Fact]
    public void Validate_rejects_incomplete_typed_context_manifest_and_tampering()
    {
        var seed = CreateRun();
        var invalidSource = seed.ContextSnapshot.SourceManifest[0] with
        {
            Order = 2,
            SourceType = CustomLoopContextSource.Unknown,
            Provenance = CustomLoopContextProvenance.Unknown,
            TrustClass = CustomLoopContextTrustClass.Unknown,
            Role = LlmMessageRole.Unknown,
            ContentHash = new string('0', 64),
            OriginalCharacterCount = -1,
            UsedCharacterCount = 99
        };
        var snapshot = new CustomLoopContextSnapshot(
            99,
            _timestamp.AddDays(1),
            [invalidSource, null!],
            "not-a-hash");

        var validation = CustomLoopRunValidator.Validate(seed with { ContextSnapshot = snapshot });

        AssertCodes(validation, "unsupported_context_schema", "invalid_context_capture_timestamp", "invalid_sha256_hash", "incomplete_workspace_context_manifest", "invalid_context_source_order", "unsupported_manifest_source_type", "unsupported_context_provenance", "unsupported_context_trust_class", "unsupported_context_role", "context_source_character_count_mismatch", "content_hash_mismatch", "invalid_workspace_context_classification", "context_manifest_source_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ContextSnapshot = null! }), "context_snapshot_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ContextSnapshot = seed.ContextSnapshot with { SourceManifest = null! } }), "context_manifest_required");
        var tampered = seed.ContextSnapshot with { SourceManifest = [WithContent(seed.ContextSnapshot.SourceManifest[0], "tampered"), .. seed.ContextSnapshot.SourceManifest.Skip(1)] };
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ContextSnapshot = tampered }), "context_manifest_hash_mismatch");
    }

    [Fact]
    public void Validate_rejects_a_pre_role_identity_manifest_without_a_legacy_fallback()
    {
        var seed = CreateRun();
        var legacyManifest = seed.ContextSnapshot.SourceManifest.ToArray();
        legacyManifest[1] = legacyManifest[1] with { SourceId = "agent", SourcePath = "C:/workspace/.agent/AGENT.md" };
        legacyManifest[2] = legacyManifest[2] with
        {
            SourceType = CustomLoopContextSource.RoleInstruction,
            Provenance = CustomLoopContextProvenance.WorkspaceRoleFile
        };
        legacyManifest[3] = legacyManifest[3] with
        {
            SourceType = CustomLoopContextSource.RoleInstruction,
            Provenance = CustomLoopContextProvenance.WorkspaceRoleFile
        };
        var legacySnapshot = CustomLoopContextSnapshotHash.Apply(seed.ContextSnapshot with { SourceManifest = legacyManifest });
        var legacyRun = CustomLoopAdmissionRequestHash.Apply(seed with { ContextSnapshot = legacySnapshot, AdmissionRequestHash = string.Empty });

        var validation = CustomLoopRunValidator.Validate(legacyRun);

        AssertCodes(validation, "invalid_workspace_context_classification");
        AssertCodes(CustomLoopRunValidator.ValidateForDispatch(legacyRun), "invalid_workspace_context_classification");
    }

    [Fact]
    public void Validate_rejects_a_mixed_current_and_legacy_workspace_manifest()
    {
        var seed = CreateRun();
        var mixedManifest = seed.ContextSnapshot.SourceManifest.ToArray();
        mixedManifest[1] = mixedManifest[1] with { SourceId = "agent", SourcePath = "C:/workspace/.agent/AGENT.md" };
        var mixedSnapshot = CustomLoopContextSnapshotHash.Apply(seed.ContextSnapshot with { SourceManifest = mixedManifest });
        var mixedRun = CustomLoopAdmissionRequestHash.Apply(seed with { ContextSnapshot = mixedSnapshot, AdmissionRequestHash = string.Empty });

        var validation = CustomLoopRunValidator.Validate(mixedRun);

        AssertCodes(validation, "invalid_workspace_context_classification");
    }

    [Fact]
    public void Validate_rejects_non_monotonic_or_incomplete_events_and_context_evidence()
    {
        var seed = CreateRun();
        var badBlock = new CustomLoopContextBlock(
            CustomLoopContextSource.Unknown,
            "",
            LlmMessageRole.Unknown,
            Included: false,
            OmissionReason: null,
            Content: "content",
            ContentHash: new string('0', 64),
            CharacterCount: 1,
            Truncated: false);
        var badEvent = new CustomLoopRunEvent(
            3,
            seed.Events[0].EventId,
            _timestamp.AddMinutes(-1),
            CustomLoopRunEventKind.NodeOutcomeObserved,
            Iteration: 0,
            StepId: null,
            Attempt: 0,
            Detail: "",
            ContextBlocks: [badBlock, null!],
            CanonicalOutput: null,
            OriginalOutputCharacterCount: null,
            CanonicalOutputTruncated: null,
            RetainedForLoopReasoning: null,
            PublishedToInvokingConversation: null,
            ConversationPublicationId: null,
            Provider: null,
            Model: null,
            ProviderResponseId: null,
            ExitDecision: null);

        var validation = CustomLoopRunValidator.Validate(seed with { Events = [seed.Events[0], badEvent] });

        AssertCodes(validation, "non_monotonic_event_sequence", "duplicate_event_id", "invalid_event_timestamp", "invalid_event_iteration", "invalid_event_attempt", "node_event_coordinates_required", "text_required", "unsupported_context_source", "unsupported_context_role", "omission_reason_required", "context_character_count_mismatch", "content_hash_mismatch", "context_block_required", "observed_output_required");
    }

    [Fact]
    public void Validate_binds_tool_authority_and_command_to_the_matching_attempt_start()
    {
        var seed = CreateRun();
        var attemptAuthority = Authority([CustomLoopToolAssignment.Read]);
        var widenedAuthority = Authority([CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search]);
        var started = new CustomLoopRunEvent(2, "attempt-start", _timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "attempt-1", null, attemptAuthority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var widenedEvidence = ToolEvidence(widenedAuthority, ToolCommand.Search);
        var widenedEvent = new CustomLoopRunEvent(3, "tool-widened", _timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, widenedAuthority, widenedEvidence);
        var unauthorizedEvidence = ToolEvidence(attemptAuthority, ToolCommand.Search);
        var unauthorizedEvent = new CustomLoopRunEvent(3, "tool-unauthorized", _timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, attemptAuthority, unauthorizedEvidence);

        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [seed.Events[0], started, widenedEvent] }), "tool_authority_not_attempt_bound", "tool_command_not_attempt_authorized");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [seed.Events[0], started, unauthorizedEvent] }), "tool_command_not_attempt_authorized");
    }

    [Fact]
    public void Validate_accepts_a_fresh_authority_snapshot_that_revokes_attempt_start_commands()
    {
        var seed = CreateRun();
        var attemptAuthority = Authority([CustomLoopToolAssignment.Read]);
        var revokedAuthority = attemptAuthority with
        {
            CurrentRoleCeiling = [],
            EffectiveAssignments = [],
            RoleCeilingHash = new string('c', CustomLoopLimits.Sha256HexCharacters),
            Detail = "Read authority was revoked before actuation."
        };
        var started = new CustomLoopRunEvent(2, "attempt-start", _timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "attempt-1", null, attemptAuthority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var evidence = ToolEvidence(revokedAuthority, ToolCommand.Read);
        var reserved = new CustomLoopRunEvent(3, "tool-revoked", _timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, revokedAuthority, evidence);

        var validation = CustomLoopRunValidator.Validate(seed with { Events = [seed.Events[0], started, reserved] });

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
    }

    [Fact]
    public void Tool_evidence_preserves_exact_well_formed_unicode_paths_without_requiring_normalization()
    {
        const string DecomposedPath = "shared/cafe\u0301.txt";
        var seed = CreateRun();
        var authority = Authority([CustomLoopToolAssignment.Read]);
        var started = new CustomLoopRunEvent(2, "attempt-start", _timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "attempt-1", null, authority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var evidence = ToolEvidence(authority, ToolCommand.Read, DecomposedPath);
        var reserved = new CustomLoopRunEvent(3, "tool-decomposed-path", _timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, authority, evidence);
        var run = seed with { Events = [seed.Events[0], started, reserved] };

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
        Assert.Equal(DecomposedPath, run.Events[^1].ToolEvidence!.TargetPath);
        var unsafeRun = run with { Events = [run.Events[0], started, reserved with { ToolEvidence = evidence with { TargetPath = "shared/\0.txt" } }] };
        AssertCodes(CustomLoopRunValidator.Validate(unsafeRun), "unsafe_text");
    }

    [Fact]
    public void Validate_accepts_the_exact_nonterminal_control_limit_with_terminal_and_warning_slots_reserved()
    {
        var run = WithLifecycleControlEvents(CreateRun(), CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun);

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun, run.Events.Count(item => item.Kind is CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning));
    }

    [Fact]
    public void Validate_rejects_a_nonterminal_run_that_consumes_the_terminal_lifecycle_slot()
    {
        var run = WithLifecycleControlEvents(CreateRun(), CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun + 1);

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.Equal(["terminal_control_slots_not_reserved"], validation.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Validate_accepts_terminalization_and_one_warning_at_the_exact_control_boundary()
    {
        var nonterminal = WithLifecycleControlEvents(CreateRun(), CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun);
        var terminal = Advance(nonterminal, CustomLoopRunStatus.Completed);
        var warning = Event(terminal.Events.Length + 1L, "event-terminal-warning", CustomLoopRunEventKind.IntegrityWarning, timestamp: terminal.UpdatedAtUtc.AddMinutes(1));
        var warningValidation = CustomLoopRunValidator.ValidateTerminalIntegrityWarningAppend(terminal, warning);
        var withWarning = terminal with
        {
            LifecycleVersion = terminal.LifecycleVersion + 1,
            UpdatedAtUtc = warning.TimestampUtc,
            Events = [.. terminal.Events, warning]
        };

        Assert.True(CustomLoopRunValidator.Validate(terminal).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(terminal).Errors));
        Assert.True(warningValidation.IsValid, string.Join(Environment.NewLine, warningValidation.Errors));
        Assert.True(CustomLoopRunValidator.Validate(withWarning).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(withWarning).Errors));
        Assert.Equal(CustomLoopLimits.MaxLifecycleControlEventsPerRun, withWarning.Events.Count(item => item.Kind is CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning));
    }

    [Fact]
    public void Validate_rejects_terminal_and_warning_shapes_that_do_not_preserve_the_exact_slots()
    {
        var terminalWithoutWarningSlot = Advance(WithLifecycleControlEvents(CreateRun(), CustomLoopLimits.MaxTerminalLifecycleControlEventsBeforeIntegrityWarning), CustomLoopRunStatus.Completed);
        var misplacedWarning = Event(2, "event-misplaced-warning", CustomLoopRunEventKind.IntegrityWarning);
        var nonterminalWithWarning = CreateRun() with { Events = [CreateRun().Events[0], misplacedWarning] };
        var tooMany = WithLifecycleControlEvents(CreateRun(), CustomLoopLimits.MaxLifecycleControlEventsPerRun + 1);

        Assert.Contains(CustomLoopRunValidator.Validate(terminalWithoutWarningSlot).Errors, error => error.Code == "integrity_warning_slot_not_reserved");
        Assert.Contains(CustomLoopRunValidator.Validate(nonterminalWithWarning).Errors, error => error.Code == "invalid_terminal_integrity_warning_placement");
        Assert.Contains(CustomLoopRunValidator.Validate(tooMany).Errors, error => error.Code == "too_many_lifecycle_control_events");
    }

    [Fact]
    public void Validate_accepts_exactly_the_trace_event_limit()
    {
        var run = WithTraceEvents(CreateRun(), CustomLoopLimits.MaxTraceEventsPerRun);

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(CustomLoopLimits.MaxTraceEventsPerRun, run.Events.Length);
        Assert.DoesNotContain(run.Events, item => item.Kind is CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning);
    }

    [Fact]
    public void Validate_rejects_one_trace_event_above_the_limit()
    {
        var run = WithTraceEvents(CreateRun(), CustomLoopLimits.MaxTraceEventsPerRun + 1);

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.Equal(["too_many_trace_events"], validation.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Validate_requires_admission_first_and_exit_or_iteration_coordinates()
    {
        var seed = CreateRun();
        var first = seed.Events[0] with { Kind = CustomLoopRunEventKind.LifecycleChanged };
        var iteration = Event(2, "event-2", CustomLoopRunEventKind.IterationStarted, iteration: null);
        var exit = Event(3, "event-3", CustomLoopRunEventKind.ExitDecisionCompleted, iteration: 1, attempt: null);
        var unknown = Event(4, "event-4", (CustomLoopRunEventKind)999);

        var validation = CustomLoopRunValidator.Validate(seed with { Events = [first, iteration, exit, unknown] });

        AssertCodes(validation, "first_event_not_admission", "iteration_coordinate_required", "exit_event_coordinates_required", "exit_decision_required", "unsupported_event_kind");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = [] }), "admission_event_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Events = null! }), "events_required");
    }

    [Fact]
    public void Validate_rejects_invalid_checkpoint_positions_outputs_and_commit_sequence()
    {
        var seed = CreateRun();
        var badOutput = new CustomLoopRetainedOutput("missing-step", 0, "output", new string('0', 64));
        var checkpoint = new CustomLoopRunCheckpoint(
            Iteration: 3,
            NextStepIndex: 99,
            AcceptedRepeatCount: 0,
            PendingExitDecision: true,
            EarlierRetainedOutputs: [badOutput, badOutput, null!],
            PreviousIterationResult: badOutput,
            CurrentIterationResult: badOutput,
            ToolRequestsUsed: 99,
            LastCommittedSequence: 1);

        var validation = CustomLoopRunValidator.Validate(seed with { Checkpoint = checkpoint });

        AssertCodes(validation, "checkpoint_iteration_out_of_range", "checkpoint_repeat_count_mismatch", "checkpoint_step_out_of_range", "invalid_pending_exit_checkpoint", "tool_request_budget_out_of_range", "unknown_retained_step", "retained_output_iteration_out_of_range", "content_hash_mismatch", "duplicate_retained_output", "retained_output_required", "invalid_current_iteration_result", "checkpoint_sequence_not_commit");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Checkpoint = null! }), "checkpoint_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { Checkpoint = seed.Checkpoint with { EarlierRetainedOutputs = null!, LastCommittedSequence = 99 } }), "retained_outputs_required", "checkpoint_sequence_out_of_range");
    }

    [Fact]
    public void Validate_enforces_status_specific_outcomes()
    {
        var seed = CreateRun();
        var completed = Advance(seed, CustomLoopRunStatus.Completed) with { FinalOutput = null, FailureCode = "failure", FailureDetail = null };
        var failed = Advance(seed, CustomLoopRunStatus.Failed) with { FinalOutput = "unexpected", FailureCode = null, FailureDetail = null };
        var running = seed with { FailureCode = "failure", FailureDetail = "detail" };

        AssertCodes(CustomLoopRunValidator.Validate(completed), "final_output_required", "unexpected_failure", "incomplete_failure_outcome");
        AssertCodes(CustomLoopRunValidator.Validate(failed), "unexpected_final_output", "failure_detail_required");
        AssertCodes(CustomLoopRunValidator.Validate(running), "unexpected_nonterminal_failure");
    }

    [Fact]
    public void ValidateUpdate_accepts_append_only_transition_and_rejects_admission_or_history_mutation()
    {
        var current = CreateRun();
        var valid = Advance(current, CustomLoopRunStatus.Running);
        Assert.True(CustomLoopRunValidator.ValidateUpdate(current, valid).IsValid);

        var changedContext = valid with { ContextSnapshot = valid.ContextSnapshot with { ManifestHash = CustomLoopTraceContentHash.Compute("changed") } };
        var changedHistory = valid with { Events = [valid.Events[0] with { Detail = "rewritten" }, valid.Events[1]] };
        var regressed = valid with
        {
            Checkpoint = valid.Checkpoint with { Iteration = 2, AcceptedRepeatCount = 1, NextStepIndex = 1, EarlierRetainedOutputs = [] },
            Events = [valid.Events[0], valid.Events[1]],
            Status = CustomLoopRunStatus.Running
        };

        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, changedContext), "admitted_context_changed");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, changedHistory), "event_history_changed");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, regressed), "repeated_iteration_not_at_start");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, valid with { LifecycleVersion = 8 }), "invalid_lifecycle_successor");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, valid with { ExecutionClock = new CustomLoopExecutionClock(-1, valid.ExecutionClock.ActiveSinceUtc) }), "execution_clock_out_of_range", "execution_clock_regressed");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(null, valid), "current_run_required");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(current, null), "run_required");
    }

    [Fact]
    public void ValidateUpdate_rejects_invalid_transition_missing_lifecycle_event_and_terminal_update()
    {
        var admitted = CreateRun();
        var invalidTransition = Advance(admitted, CustomLoopRunStatus.PauseRequested);
        var noLifecycleEvent = Advance(admitted, CustomLoopRunStatus.Running) with { Events = admitted.Events };
        var terminal = Advance(Advance(admitted, CustomLoopRunStatus.Running), CustomLoopRunStatus.Completed);
        var terminalCandidate = terminal with { LifecycleVersion = terminal.LifecycleVersion + 1, UpdatedAtUtc = terminal.UpdatedAtUtc.AddMinutes(1) };

        AssertCodes(CustomLoopRunValidator.ValidateUpdate(admitted, invalidTransition), "invalid_lifecycle_transition");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(admitted, noLifecycleEvent), "lifecycle_event_required");
        AssertCodes(CustomLoopRunValidator.ValidateUpdate(terminal, terminalCandidate), "terminal_run_immutable");
    }

    [Theory]
    [InlineData(CustomLoopRunStatus.Admitted, CustomLoopRunStatus.Running, true)]
    [InlineData(CustomLoopRunStatus.Running, CustomLoopRunStatus.PauseRequested, true)]
    [InlineData(CustomLoopRunStatus.PauseRequested, CustomLoopRunStatus.Paused, true)]
    [InlineData(CustomLoopRunStatus.Paused, CustomLoopRunStatus.Running, true)]
    [InlineData(CustomLoopRunStatus.Paused, CustomLoopRunStatus.CancelRequested, true)]
    [InlineData(CustomLoopRunStatus.Paused, CustomLoopRunStatus.Cancelled, true)]
    [InlineData(CustomLoopRunStatus.Paused, CustomLoopRunStatus.NeedsReview, true)]
    [InlineData(CustomLoopRunStatus.CancelRequested, CustomLoopRunStatus.Cancelled, true)]
    [InlineData(CustomLoopRunStatus.Completed, CustomLoopRunStatus.Running, false)]
    [InlineData(CustomLoopRunStatus.Admitted, CustomLoopRunStatus.PauseRequested, false)]
    public void Lifecycle_transition_table_is_explicit(CustomLoopRunStatus current, CustomLoopRunStatus next, bool expected)
    {
        Assert.Equal(expected, CustomLoopRunValidator.IsAllowedLifecycleTransition(current, next));
        Assert.True(CustomLoopRunValidator.IsAllowedLifecycleTransition(current, current));
    }

    private static CustomLoopRunRecord CreateRun(string loopId = "loop-alpha", string runId = "run-alpha", string operationId = "invoke-alpha")
    {
        var definition = CustomLoopDefinition.CreateSeed(loopId, "default-role", "step-1", "create-loop", _timestamp);
        var snapshot = CustomLoopContextSnapshotHash.Apply(new CustomLoopContextSnapshot(
            CustomLoopContextSnapshot.CurrentSchemaVersion,
            _timestamp,
            CreateManifest("Role context"),
            string.Empty));
        var admitted = Event(1, "event-1", CustomLoopRunEventKind.Admitted);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            runId,
            loopId,
            1,
            CustomLoopRunStatus.Admitted,
            _timestamp,
            _timestamp,
            null,
            "web",
            new CustomLoopModelSnapshot("openai", "gpt-5"),
            operationId,
            "embodysense.web",
            string.Empty,
            definition,
            "Initial prompt",
            null,
            snapshot,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admitted],
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _timestamp)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord CreateSequentialRun()
    {
        var run = CreateRun();
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            run.TriggerPrompt,
            run.ModelSnapshot,
            run.InvokingConversation,
            run.ContextSnapshot.CapturedAtUtc,
            run.ContextSnapshot.SourceManifest,
            string.Empty));
        var revision = GovernedLoopRevisionReference.Create(1, "graph-alpha", "revision-alpha", new string('a', 64));
        var execution = GovernedLoopExecutionBinding.Create(1, run.Id, revision, 1);
        var workspaceId = "workspace-sha256:" + new string('b', 64);
        var graphArtifactHash = new string('e', 64);
        var capabilityAdmission = CreateSequentialCapabilityAdmission(graphArtifactHash, [ConversationTurnCapabilityId, ModelInferenceCapabilityId]) with { WorkspaceScopeId = workspaceId };
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, revision, "publish-alpha", new string('7', 64));
        var intent = GovernedLoopAdmissionTestFixture.Intent(
            workspaceId: workspaceId,
            operationId: run.AdmissionOperationId,
            requestHash: new string('d', 64),
            publication: publication,
            graphArtifactHash: graphArtifactHash,
            graphLayoutHash: new string('f', 64));
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(
            intent,
            GovernedLoopAdmissionTestFixture.Evidence(intent, binding: execution, capabilityAdmission: capabilityAdmission));
        var binding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            workspaceId,
            execution,
            run.AdmissionOperationId,
            receipt,
            receipt.ContentHash,
            new string('d', 64),
            invocation.ContentHash,
            graphArtifactHash,
            new string('f', 64),
            string.Empty));
        var admitted = WithSequentialEvidence(
            run.Events[0],
            binding,
            "trigger-node",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var frontier = CreateInitialFrontier(binding, admitted);
        return CustomLoopAdmissionRequestHash.Apply(run with
        {
            CapabilityAdmission = capabilityAdmission,
            SequentialInvocationSnapshot = invocation,
            SequentialAdapterBinding = binding,
            Frontier = frontier,
            Events = [admitted],
        });
    }

    private static CapabilityAdmissionSnapshot CreateSequentialCapabilityAdmission(
        GovernedLoopSequentialAdapterBinding binding,
        IReadOnlyList<string> capabilityIds,
        string? graphArtifactHash = null)
        => CreateSequentialCapabilityAdmission(binding.GraphArtifactHash, capabilityIds, graphArtifactHash) with { WorkspaceScopeId = binding.WorkspaceId };

    private static CapabilityAdmissionSnapshot CreateSequentialCapabilityAdmission(
        string admittedGraphArtifactHash,
        IReadOnlyList<string> capabilityIds,
        string? graphArtifactHash = null)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-" + admittedGraphArtifactHash[..32], out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var compatibleVersions, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + (graphArtifactHash ?? admittedGraphArtifactHash), out var checksum, out _));
        var dependencies = capabilityIds.Select(capabilityId =>
        {
            Assert.True(CapabilityId.TryParse(capabilityId, out var dependency, out _));
            return new CapabilityDependency(dependency!, compatibleVersions!);
        }).ToArray();
        var requirements = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            dependencies,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        return TestCapabilityAdmissionFactory.Create(requirements, _timestamp);
    }

    private static CustomLoopRunRecord WithSequentialToolAssignments(
        CustomLoopRunRecord run,
        CustomLoopToolAssignment[] assignments,
        IReadOnlyList<string> capabilityIds)
    {
        var definition = run.AdmittedDefinition with
        {
            ToolAssignments = assignments,
            ContentHash = string.Empty,
        };
        definition = CustomLoopDefinitionContentHash.Apply(definition with
        {
            CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest(definition.Id, assignments),
        });
        var capabilityAdmission = CreateSequentialCapabilityAdmission(run.SequentialAdapterBinding!, capabilityIds);
        var binding = WithCapabilityAdmission(run.SequentialAdapterBinding!, capabilityAdmission);
        return CustomLoopAdmissionRequestHash.Apply(run with
        {
            AdmittedDefinition = definition,
            CapabilityAdmission = capabilityAdmission,
            SequentialAdapterBinding = binding,
            Frontier = RebindFrontier(run.Frontier!, binding),
        });
    }

    private static GovernedLoopFrontierPosture CreateInitialFrontier(GovernedLoopSequentialAdapterBinding binding, CustomLoopRunEvent admitted)
    {
        const string ControlEdgeId = "edge-trigger-node-step-1";
        var trigger = GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "trigger-node",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
            [],
            [ControlEdgeId],
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            "attempt-trigger-node-1",
            admitted.EventId,
            admitted.SequentialNodeEvidence!.OutcomeArtifactHash,
            controlOutcome: GovernedLoopControlCondition.Always,
            selectedControlEdgeIds: [ControlEdgeId]);
        var inference = GovernedLoopNodeExecutionEvidence.Create(
            1,
            "step-1",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
            [ControlEdgeId],
            [],
            GovernedLoopNodeExecutionStatus.Ready);
        var payload = GovernedLoopFrontierPayload.Create(
            1,
            1,
            GovernedLoopExecutionLimits.Schema1ConcurrencyCeiling,
            GovernedLoopFrontierStatus.Active,
            [trigger, inference],
            admitted.TimestampUtc,
            string.Empty);
        return GovernedLoopFrontierPosture.Create(
            binding.ExecutionBinding,
            binding.WorkspaceId,
            binding.GraphArtifactHash,
            binding.GraphLayoutHash,
            binding.AdmissionReceiptHash,
            payload);
    }

    private static CustomLoopRunRecord WithPureFrontier(
        CustomLoopRunRecord run,
        string nodeId,
        GovernedLoopNodeKind kind = GovernedLoopNodeKind.Transform)
    {
        var current = run.Frontier!;
        var source = current.Payload.Nodes[1];
        var pure = GovernedLoopNodeExecutionEvidence.Create(
            source.PlanOrdinal,
            nodeId,
            new GovernedLoopNodeDescriptor(kind, kind == GovernedLoopNodeKind.Transform ? "identity" : "schema-conformance", 1),
            source.IncomingControlEdgeIds,
            source.OutgoingControlEdgeIds,
            source.Status);
        var payload = GovernedLoopFrontierPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.FrontierVersion,
            current.Payload.ConcurrencyCeiling,
            current.Payload.Status,
            [current.Payload.Nodes[0], pure],
            current.Payload.UpdatedAtUtc,
            string.Empty);
        return run with
        {
            Frontier = GovernedLoopFrontierPosture.Create(
                current.Binding,
                current.WorkspaceId,
                current.GraphArtifactHash,
                current.GraphLayoutHash,
                current.AdmissionReceiptHash,
                payload),
        };
    }

    private static CustomLoopRunRecord CreateSkippedRun(CustomLoopRunRecord run)
    {
        const string SelectedEdgeId = "edge-trigger-node-exit";
        const string SkippedEdgeId = "edge-trigger-node-step-1";
        var binding = run.SequentialAdapterBinding!;
        var admitted = WithSequentialEvidence(
            run.Events[0] with { SequentialNodeEvidence = null },
            binding,
            "trigger-node",
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed,
            selectedControlEdgeIds: [SelectedEdgeId],
            skippedControlEdgeIds: [SkippedEdgeId]);
        var skipEvent = WithSequentialEvidence(
            Event(2, "skip-step-1", CustomLoopRunEventKind.TopologyNodeSkipped) with { StepId = "step-1" },
            binding,
            "step-1",
            null,
            CustomLoopSequentialNodeEvidenceKind.TopologySkipped,
            CustomLoopSequentialNodeDisposition.Completed,
            governingActivationOrdinal: 0,
            governingControlEdgeId: SkippedEdgeId);
        var trigger = GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "trigger-node",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
            [],
            [SelectedEdgeId, SkippedEdgeId],
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            "attempt-trigger-node-1",
            admitted.EventId,
            admitted.SequentialNodeEvidence!.OutcomeArtifactHash,
            controlOutcome: GovernedLoopControlCondition.Always,
            selectedControlEdgeIds: [SelectedEdgeId],
            skippedControlEdgeIds: [SkippedEdgeId]);
        var skipped = GovernedLoopNodeExecutionEvidence.CreateActivation(
            1,
            1,
            1,
            "step-1",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
            [SkippedEdgeId],
            [],
            GovernedLoopNodeExecutionStatus.Skipped,
            outcomeEvidenceId: skipEvent.EventId,
            outcomeEvidenceHash: skipEvent.SequentialNodeEvidence!.OutcomeArtifactHash);
        var exit = GovernedLoopNodeExecutionEvidence.CreateActivation(
            2,
            2,
            1,
            "exit",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
            [SelectedEdgeId],
            [],
            GovernedLoopNodeExecutionStatus.Ready);
        var payload = GovernedLoopFrontierPayload.Create(
            run.Frontier!.Payload.SchemaVersion,
            run.Frontier.Payload.FrontierVersion,
            run.Frontier.Payload.ConcurrencyCeiling,
            GovernedLoopFrontierStatus.Active,
            [trigger, skipped, exit],
            run.Frontier.Payload.UpdatedAtUtc,
            string.Empty);
        var frontier = GovernedLoopFrontierPosture.Create(
            run.Frontier.Binding,
            run.Frontier.WorkspaceId,
            run.Frontier.GraphArtifactHash,
            run.Frontier.GraphLayoutHash,
            run.Frontier.AdmissionReceiptHash,
            payload);
        return CustomLoopAdmissionRequestHash.Apply(run with { Events = [admitted, skipEvent], Frontier = frontier });
    }

    private static GovernedLoopFrontierPosture RebindFrontier(GovernedLoopFrontierPosture frontier, GovernedLoopSequentialAdapterBinding binding)
    {
        var payload = GovernedLoopFrontierPayload.Create(
            frontier.Payload.SchemaVersion,
            frontier.Payload.FrontierVersion,
            frontier.Payload.ConcurrencyCeiling,
            frontier.Payload.Status,
            frontier.Payload.Nodes,
            frontier.Payload.UpdatedAtUtc,
            string.Empty);
        return GovernedLoopFrontierPosture.Create(
            binding.ExecutionBinding,
            binding.WorkspaceId,
            binding.GraphArtifactHash,
            binding.GraphLayoutHash,
            binding.AdmissionReceiptHash,
            payload);
    }

    private static CustomLoopRunRecord CreateRunningSequentialRun(CustomLoopRunRecord admitted)
    {
        var runningSeed = Advance(admitted, CustomLoopRunStatus.Running);
        var dispatch = SequentialEvent(
            admitted.Events.Length + 1L,
            "inference-start",
            CustomLoopRunEventKind.NodeAttemptStarted,
            admitted.SequentialAdapterBinding!,
            "step-1",
            "step-1",
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown,
            runningSeed.UpdatedAtUtc);
        var lifecycle = runningSeed.Events[^1] with
        {
            Sequence = dispatch.Sequence + 1,
            EventId = "event-running-lifecycle",
        };
        return runningSeed with
        {
            Events = [.. admitted.Events, dispatch, lifecycle],
            Frontier = TransitionInferenceFrontier(
                admitted.Frontier!,
                GovernedLoopFrontierStatus.Active,
                GovernedLoopNodeExecutionStatus.Running,
                runningSeed.UpdatedAtUtc,
                attemptOperationId: dispatch.EventId),
        };
    }

    private static CustomLoopRunRecord WithAttemptEvidence(
        CustomLoopRunRecord run,
        CustomLoopRunEvent start,
        CustomLoopRunEvent outcome,
        GovernedLoopNodeExecutionStatus outcomeStatus)
    {
        return run with
        {
            Events = [run.Events[0], start, outcome],
            Frontier = TransitionInferenceFrontier(
                run.Frontier!,
                GovernedLoopFrontierStatus.Active,
                outcomeStatus,
                run.UpdatedAtUtc,
                outcome,
                start.EventId,
                retainReadySuccessor: true),
        };
    }

    private static CustomLoopRunRecord WithExitAttemptEvidence(
        CustomLoopRunRecord run,
        CustomLoopRunEvent start,
        CustomLoopRunEvent outcome,
        GovernedLoopNodeExecutionStatus outcomeStatus)
    {
        var evidence = outcome.SequentialNodeEvidence!;
        var exit = GovernedLoopNodeExecutionEvidence.CreateActivation(
            evidence.ActivationOrdinal,
            2,
            evidence.VisitOrdinal,
            evidence.NodeId,
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
            [],
            [],
            outcomeStatus,
            evidence.Attempt,
            start.EventId,
            outcome.EventId,
            evidence.OutcomeArtifactHash,
            evidence.CycleId,
            evidence.CycleIteration,
            evidence.ControlOutcome,
            evidence.SelectedControlEdgeIds,
            evidence.SkippedControlEdgeIds);
        var current = run.Frontier!;
        var payload = GovernedLoopFrontierPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.FrontierVersion,
            current.Payload.ConcurrencyCeiling,
            current.Payload.Status,
            [.. current.Payload.Nodes, exit],
            current.Payload.UpdatedAtUtc,
            string.Empty);
        return run with
        {
            Events = [run.Events[0], start, outcome],
            Frontier = GovernedLoopFrontierPosture.Create(
                current.Binding,
                current.WorkspaceId,
                current.GraphArtifactHash,
                current.GraphLayoutHash,
                current.AdmissionReceiptHash,
                payload),
        };
    }

    private static GovernedLoopFrontierPosture ReplaceTriggerOutcome(GovernedLoopFrontierPosture current, string outcomeEvidenceId, string outcomeEvidenceHash)
    {
        var trigger = current.Payload.Nodes[0];
        var replacement = GovernedLoopNodeExecutionEvidence.CreateActivation(
            trigger.ActivationOrdinal,
            trigger.PlanOrdinal,
            trigger.VisitOrdinal,
            trigger.NodeId,
            trigger.Descriptor,
            trigger.IncomingControlEdgeIds,
            trigger.OutgoingControlEdgeIds,
            trigger.Status,
            trigger.Attempt,
            trigger.AttemptOperationId,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            trigger.CycleId,
            trigger.CycleIteration,
            trigger.ControlOutcome,
            trigger.SelectedControlEdgeIds,
            trigger.SkippedControlEdgeIds,
            trigger.JoinArrivals);
        var payload = GovernedLoopFrontierPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.FrontierVersion,
            current.Payload.ConcurrencyCeiling,
            current.Payload.Status,
            [replacement, .. current.Payload.Nodes.Skip(1)],
            current.Payload.UpdatedAtUtc,
            string.Empty);
        return GovernedLoopFrontierPosture.Create(
            current.Binding,
            current.WorkspaceId,
            current.GraphArtifactHash,
            current.GraphLayoutHash,
            current.AdmissionReceiptHash,
            payload);
    }

    private static GovernedLoopFrontierPosture TransitionInferenceFrontier(
        GovernedLoopFrontierPosture current,
        GovernedLoopFrontierStatus frontierStatus,
        GovernedLoopNodeExecutionStatus nodeStatus,
        DateTimeOffset updatedAtUtc,
        CustomLoopRunEvent? outcomeEvent = null,
        string? attemptOperationId = null,
        bool retainReadySuccessor = false)
    {
        var currentNode = current.Payload.Nodes[1];
        var hasAttempt = nodeStatus is not (GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Skipped);
        var transitionedNode = GovernedLoopNodeExecutionEvidence.CreateActivation(
            currentNode.ActivationOrdinal,
            currentNode.PlanOrdinal,
            currentNode.VisitOrdinal,
            currentNode.NodeId,
            currentNode.Descriptor,
            currentNode.IncomingControlEdgeIds,
            currentNode.OutgoingControlEdgeIds,
            nodeStatus,
            hasAttempt ? 1 : null,
            hasAttempt ? attemptOperationId ?? currentNode.AttemptOperationId ?? "attempt-step-1-1" : null,
            outcomeEvent?.EventId,
            outcomeEvent?.SequentialNodeEvidence?.OutcomeArtifactHash,
            currentNode.CycleId,
            currentNode.CycleIteration,
            outcomeEvent?.SequentialNodeEvidence?.ControlOutcome,
            outcomeEvent?.SequentialNodeEvidence?.SelectedControlEdgeIds,
            outcomeEvent?.SequentialNodeEvidence?.SkippedControlEdgeIds,
            currentNode.JoinArrivals);
        var nodes = retainReadySuccessor
            ? new[]
            {
                current.Payload.Nodes[0],
                transitionedNode,
                GovernedLoopNodeExecutionEvidence.CreateActivation(
                    2,
                    2,
                    1,
                    "post-attempt-exit",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                    [],
                    [],
                    GovernedLoopNodeExecutionStatus.Ready),
            }
            : [current.Payload.Nodes[0], transitionedNode];
        var payload = GovernedLoopFrontierPayload.Create(
            1,
            current.Payload.FrontierVersion + 1,
            current.Payload.ConcurrencyCeiling,
            frontierStatus,
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

    private static CustomLoopRunEvent[] ReplaceEvent(
        IReadOnlyList<CustomLoopRunEvent> events,
        int index,
        CustomLoopRunEvent replacement)
    {
        var result = events.ToArray();
        result[index] = replacement;
        return result;
    }

    private static GovernedLoopSequentialAdapterBinding WithCapabilityAdmission(
        GovernedLoopSequentialAdapterBinding binding,
        CapabilityAdmissionSnapshot capabilityAdmission)
    {
        var receipt = binding.AdmissionReceipt;
        var source = receipt.Evidence;
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            source.SchemaVersion,
            source.IntentHash,
            source.Binding,
            source.GrantProfile,
            source.GrantBoundary,
            source.GrantDependencyEvidenceHash,
            source.EffectiveAuthority,
            capabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(receipt.Intent, source.EffectiveAuthority, capabilityAdmission),
            source.EvaluatedAtUtc,
            string.Empty));
        receipt = GovernedLoopAdmissionContractHash.Apply(receipt with { Evidence = evidence, ContentHash = string.Empty });
        return GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            binding.SchemaVersion,
            binding.WorkspaceId,
            binding.ExecutionBinding,
            binding.AdmissionOperationId,
            receipt,
            receipt.ContentHash,
            binding.AdmissionRequestHash,
            binding.InvocationPayloadHash,
            binding.GraphArtifactHash,
            binding.GraphLayoutHash,
            string.Empty));
    }

    private static string GetRequirementsHash(CustomLoopDefinition definition)
    {
        Assert.True(CapabilityDependencyManifestHash.TryCompute(definition.CapabilityRequirements, out var hash, out _));
        return hash!.Value;
    }

    private static CustomLoopRunEvent WithSequentialEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        string nodeId,
        int? attempt,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition,
        IReadOnlyList<string>? selectedControlEdgeIds = null,
        IReadOnlyList<string>? skippedControlEdgeIds = null,
        int? governingActivationOrdinal = null,
        string? governingControlEdgeId = null)
    {
        const string TriggerControlEdgeId = "edge-trigger-node-step-1";
        var isTrigger = string.Equals(nodeId, "trigger-node", StringComparison.Ordinal);
        var activationOrdinal = isTrigger
            ? 0
            : nodeId.Contains("exit", StringComparison.Ordinal) ? 2 : 1;
        var controlOutcome = kind switch
        {
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted or CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention or CustomLoopSequentialNodeEvidenceKind.TopologySkipped => (GovernedLoopControlCondition?)null,
            _ when isTrigger => GovernedLoopControlCondition.Always,
            _ when disposition == CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopControlCondition.Failure,
            _ => GovernedLoopControlCondition.Success,
        };
        selectedControlEdgeIds ??= isTrigger && kind != CustomLoopSequentialNodeEvidenceKind.DispatchStarted
            ? [TriggerControlEdgeId]
            : [];
        skippedControlEdgeIds ??= [];
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
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
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            governingActivationOrdinal,
            governingControlEdgeId,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent SequentialEvent(
        long sequence,
        string eventId,
        CustomLoopRunEventKind eventKind,
        GovernedLoopSequentialAdapterBinding binding,
        string nodeId,
        string stepId,
        CustomLoopSequentialNodeEvidenceKind evidenceKind,
        CustomLoopSequentialNodeDisposition disposition,
        DateTimeOffset? timestamp = null)
    {
        var runEvent = Event(sequence, eventId, eventKind, iteration: 1, attempt: 1, timestamp: timestamp) with
        {
            StepId = stepId,
            ExitDecision = eventKind == CustomLoopRunEventKind.ExitDecisionCompleted ? CustomLoopExitDecision.Complete : null,
            TraceReservationUtf8Bytes = eventKind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted
                ? CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes
                : null,
        };
        return WithSequentialEvidence(runEvent, binding, nodeId, 1, evidenceKind, disposition);
    }

    private static CustomLoopRunEvent PureSequentialEvent(
        long sequence,
        string eventId,
        CustomLoopRunEventKind eventKind,
        GovernedLoopSequentialAdapterBinding binding,
        string nodeId,
        string? outcomeJson = null,
        CustomLoopSequentialNodeEvidenceKind evidenceKind = CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
        CustomLoopSequentialNodeDisposition disposition = CustomLoopSequentialNodeDisposition.Completed,
        int? reservation = null,
        string? provider = null)
    {
        var runEvent = Event(sequence, eventId, eventKind, iteration: 1, attempt: 1) with
        {
            StepId = nodeId,
            Provider = provider,
            PureNodeOutcomeJson = outcomeJson,
            TraceReservationUtf8Bytes = eventKind == CustomLoopRunEventKind.NodeAttemptStarted
                ? reservation ?? CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes
                : null,
        };
        var actualKind = eventKind == CustomLoopRunEventKind.NodeAttemptStarted
            ? CustomLoopSequentialNodeEvidenceKind.DispatchStarted
            : evidenceKind;
        var actualDisposition = eventKind == CustomLoopRunEventKind.NodeAttemptStarted
            ? CustomLoopSequentialNodeDisposition.Unknown
            : disposition;
        return WithSequentialEvidence(runEvent, binding, nodeId, 1, actualKind, actualDisposition);
    }

    private static CustomLoopToolAuthoritySnapshot Authority(CustomLoopToolAssignment[] effectiveAssignments)
    {
        var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
        return new CustomLoopToolAuthoritySnapshot("default-role", effectiveAssignments, effectiveAssignments, catalog, effectiveAssignments, new string('a', CustomLoopLimits.Sha256HexCharacters), new string('b', CustomLoopLimits.Sha256HexCharacters), _timestamp, true, "Test authority snapshot.");
    }

    private static CustomLoopToolTraceEvidence ToolEvidence(CustomLoopToolAuthoritySnapshot authority, ToolCommand command, string targetPath = "shared/file.txt")
    {
        return new CustomLoopToolTraceEvidence(CustomLoopToolEvidencePhase.RequestReserved, 1, "tool-correlation", null, command, targetPath, null, null, null, authority, null, null, null, null, null, false, CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
    }

    private static CustomLoopRunRecord Advance(CustomLoopRunRecord run, CustomLoopRunStatus status)
    {
        var updatedAt = run.UpdatedAtUtc.AddMinutes(1);
        var lifecycle = Event(run.Events.Length + 1L, $"event-{run.Events.Length + 1}", CustomLoopRunEventKind.LifecycleChanged, timestamp: updatedAt);
        var terminal = status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = updatedAt,
            CompletedAtUtc = terminal ? updatedAt : null,
            ExecutionClock = status is CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested
                ? new CustomLoopExecutionClock(run.ExecutionClock.AccumulatedRunningMilliseconds, updatedAt)
                : new CustomLoopExecutionClock(run.ExecutionClock.AccumulatedRunningMilliseconds + (run.ExecutionClock.ActiveSinceUtc is null ? 0 : 1_000), null),
            Events = [.. run.Events, lifecycle],
            FinalOutput = status == CustomLoopRunStatus.Completed ? "done" : null,
            FailureCode = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? "failure" : null,
            FailureDetail = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? "Safe failure detail" : null
        };
    }

    private static CustomLoopRunEvent Event(long sequence, string id, CustomLoopRunEventKind kind, int? iteration = null, int? attempt = null, DateTimeOffset? timestamp = null)
    {
        return new CustomLoopRunEvent(sequence, id, timestamp ?? _timestamp, kind, iteration, null, attempt, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null);
    }

    private static CustomLoopRunRecord WithLifecycleControlEvents(CustomLoopRunRecord run, int eventCount)
    {
        var events = Enumerable.Range(2, eventCount).Select(sequence => Event(sequence, $"event-{sequence}", CustomLoopRunEventKind.LifecycleChanged)).ToArray();
        return run with { Events = [run.Events[0], .. events] };
    }

    private static CustomLoopRunRecord WithTraceEvents(CustomLoopRunRecord run, int totalEventCount)
    {
        var events = Enumerable.Range(2, totalEventCount - 1)
            .Select(sequence => Event(sequence, $"event-{sequence}", CustomLoopRunEventKind.NodeAttemptCompleted, iteration: 1, attempt: 1) with { StepId = "step-1" })
            .ToArray();
        return run with { Events = [run.Events[0], .. events] };
    }

    private static CustomLoopContextManifestSource[] CreateManifest(string roleContent)
    {
        return
        [
            Source(1, CustomLoopContextSource.RoleInstruction, "nearest-agents", "C:/workspace/AGENTS.md", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, roleContent),
            OmittedSource(2, CustomLoopContextSource.RoleInstruction, "role", "C:/workspace/.agent/ROLE.md", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            OmittedSource(3, CustomLoopContextSource.AgentIdentity, "soul", "C:/workspace/.agent/SOUL.md", CustomLoopContextProvenance.WorkspaceAgentIdentityFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            OmittedSource(4, CustomLoopContextSource.AgentIdentity, "personality", "C:/workspace/.agent/PERSONALITY.md", CustomLoopContextProvenance.WorkspaceAgentIdentityFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            OmittedSource(5, CustomLoopContextSource.ContextualState, "context", "C:/workspace/.agent/CONTEXT.md", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User),
            OmittedSource(6, CustomLoopContextSource.ContextualState, "memory", "C:/workspace/.agent/MEMORY.md", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User),
            OmittedSource(7, CustomLoopContextSource.ContextualState, "models", "C:/workspace/.agent/models.json", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User)
        ];
    }

    private static CustomLoopContextManifestSource Source(
        int order,
        CustomLoopContextSource sourceType,
        string sourceId,
        string sourcePath,
        CustomLoopContextProvenance provenance,
        CustomLoopContextTrustClass trustClass,
        LlmMessageRole role,
        string content)
    {
        return new CustomLoopContextManifestSource(order, sourceType, sourceId, sourcePath, provenance, trustClass, role, content, CustomLoopTraceContentHash.Compute(content), content.Length, content.Length, false, null, null, _timestamp);
    }

    private static CustomLoopContextManifestSource OmittedSource(
        int order,
        CustomLoopContextSource sourceType,
        string sourceId,
        string sourcePath,
        CustomLoopContextProvenance provenance,
        CustomLoopContextTrustClass trustClass,
        LlmMessageRole role)
    {
        return new CustomLoopContextManifestSource(order, sourceType, sourceId, sourcePath, provenance, trustClass, role, string.Empty, CustomLoopTraceContentHash.Compute(string.Empty), 0, 0, false, null, "Source absent in test fixture.", _timestamp);
    }

    private static CustomLoopContextManifestSource WithContent(CustomLoopContextManifestSource source, string content)
    {
        return source with
        {
            Content = content,
            ContentHash = CustomLoopTraceContentHash.Compute(content),
            OriginalCharacterCount = content.Length,
            UsedCharacterCount = content.Length,
            Truncated = false,
            TruncationReason = null,
            OmissionReason = null
        };
    }

    private static void AssertCodes(CustomLoopValidationResult validation, params string[] expectedCodes)
    {
        foreach (var code in expectedCodes)
        {
            Assert.Contains(validation.Errors, error => error.Code == code);
        }
    }
}
