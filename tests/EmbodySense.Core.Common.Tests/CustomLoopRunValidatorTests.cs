using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.Tests;

public sealed class CustomLoopRunValidatorTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");

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
    }

    [Fact]
    public void Validate_requires_pinned_model_admission_hash_and_consistent_execution_clock()
    {
        var seed = CreateRun();
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ModelSnapshot = null! }), "model_snapshot_required", "admission_request_hash_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { AdmissionRequestHash = new string('0', 64) }), "admission_request_hash_mismatch");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ExecutionClock = null! }), "execution_clock_required");
        AssertCodes(CustomLoopRunValidator.Validate(seed with { ExecutionClock = new CustomLoopExecutionClock(-1, Timestamp) }), "execution_clock_out_of_range", "unexpected_active_execution_clock");
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
        const string decomposed = "e\u0301";
        const string composed = "é";
        var seed = CreateRun();
        var decomposedSource = WithContent(seed.ContextSnapshot.SourceManifest[0], decomposed);
        var snapshot = CustomLoopContextSnapshotHash.Apply(seed.ContextSnapshot with { SourceManifest = [decomposedSource, .. seed.ContextSnapshot.SourceManifest.Skip(1)] });
        var run = CustomLoopAdmissionRequestHash.Apply(seed with { ContextSnapshot = snapshot });
        var observed = new CustomLoopRunEvent(2, "event-2", Timestamp, CustomLoopRunEventKind.NodeOutcomeObserved, 1, "step-1", 1, "Observed", [], decomposed, decomposed.Length, false, true, false, null, "openai", "gpt-5", "response-1", null);
        run = run with { Events = [.. run.Events, observed] };
        var completed = Advance(Advance(run, CustomLoopRunStatus.Running), CustomLoopRunStatus.Completed) with { FinalOutput = decomposed };

        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        Assert.True(CustomLoopRunValidator.Validate(completed).IsValid);
        Assert.True(CustomLoopContextSnapshotHash.Matches(snapshot));
        var composedSource = WithContent(snapshot.SourceManifest[0], composed);
        Assert.NotEqual(CustomLoopContextSnapshotHash.Compute(snapshot), CustomLoopContextSnapshotHash.Compute(snapshot with { SourceManifest = [composedSource, .. snapshot.SourceManifest.Skip(1)] }));
        Assert.NotEqual(CustomLoopTraceContentHash.Compute(decomposed), CustomLoopTraceContentHash.Compute(composed));
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
            CreatedAtUtc = Timestamp.AddMinutes(2).ToOffset(TimeSpan.FromHours(1)),
            UpdatedAtUtc = Timestamp,
            CompletedAtUtc = Timestamp,
            Surface = "Web/UI",
            AdmissionOperationId = "bad operation",
            AdmittedDefinition = invalidDefinition,
            TriggerPrompt = new string('x', CustomLoopLimits.MaxPresetPromptCharacters + 1),
            InvokingConversation = new CustomLoopConversationReference("../conversation", "", Timestamp.AddDays(1))
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
            Timestamp.AddDays(1),
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
            Timestamp.AddMinutes(-1),
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
        var started = new CustomLoopRunEvent(2, "attempt-start", Timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "attempt-1", null, attemptAuthority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var widenedEvidence = ToolEvidence(widenedAuthority, ToolCommand.Search);
        var widenedEvent = new CustomLoopRunEvent(3, "tool-widened", Timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, widenedAuthority, widenedEvidence);
        var unauthorizedEvidence = ToolEvidence(attemptAuthority, ToolCommand.Search);
        var unauthorizedEvent = new CustomLoopRunEvent(3, "tool-unauthorized", Timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, attemptAuthority, unauthorizedEvidence);

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
        var started = new CustomLoopRunEvent(2, "attempt-start", Timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "attempt-1", null, attemptAuthority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var evidence = ToolEvidence(revokedAuthority, ToolCommand.Read);
        var reserved = new CustomLoopRunEvent(3, "tool-revoked", Timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, revokedAuthority, evidence);

        var validation = CustomLoopRunValidator.Validate(seed with { Events = [seed.Events[0], started, reserved] });

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
    }

    [Fact]
    public void Tool_evidence_preserves_exact_well_formed_unicode_paths_without_requiring_normalization()
    {
        const string decomposedPath = "shared/cafe\u0301.txt";
        var seed = CreateRun();
        var authority = Authority([CustomLoopToolAssignment.Read]);
        var started = new CustomLoopRunEvent(2, "attempt-start", Timestamp, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-1", 1, "Attempt started.", [], null, null, null, null, null, null, "openai", "gpt-5", "attempt-1", null, authority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var evidence = ToolEvidence(authority, ToolCommand.Read, decomposedPath);
        var reserved = new CustomLoopRunEvent(3, "tool-decomposed-path", Timestamp, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-1", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, authority, evidence);
        var run = seed with { Events = [seed.Events[0], started, reserved] };

        var validation = CustomLoopRunValidator.Validate(run);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
        Assert.Equal(decomposedPath, run.Events[^1].ToolEvidence!.TargetPath);
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
        var definition = CustomLoopDefinition.CreateSeed(loopId, "default-role", "step-1", "create-loop", Timestamp);
        var snapshot = CustomLoopContextSnapshotHash.Apply(new CustomLoopContextSnapshot(
            CustomLoopContextSnapshot.CurrentSchemaVersion,
            Timestamp,
            CreateManifest("Role context"),
            string.Empty));
        var admitted = Event(1, "event-1", CustomLoopRunEventKind.Admitted);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            runId,
            loopId,
            1,
            CustomLoopRunStatus.Admitted,
            Timestamp,
            Timestamp,
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
            null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopToolAuthoritySnapshot Authority(CustomLoopToolAssignment[] effectiveAssignments)
    {
        var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
        return new CustomLoopToolAuthoritySnapshot("default-role", effectiveAssignments, effectiveAssignments, catalog, effectiveAssignments, new string('a', CustomLoopLimits.Sha256HexCharacters), new string('b', CustomLoopLimits.Sha256HexCharacters), Timestamp, true, "Test authority snapshot.");
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
        return new CustomLoopRunEvent(sequence, id, timestamp ?? Timestamp, kind, iteration, null, attempt, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null);
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
            OmittedSource(2, CustomLoopContextSource.RoleInstruction, "agent", "C:/workspace/.agent/AGENT.md", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            OmittedSource(3, CustomLoopContextSource.RoleInstruction, "soul", "C:/workspace/.agent/SOUL.md", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
            OmittedSource(4, CustomLoopContextSource.RoleInstruction, "personality", "C:/workspace/.agent/PERSONALITY.md", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System),
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
        return new CustomLoopContextManifestSource(order, sourceType, sourceId, sourcePath, provenance, trustClass, role, content, CustomLoopTraceContentHash.Compute(content), content.Length, content.Length, false, null, null, Timestamp);
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
        return new CustomLoopContextManifestSource(order, sourceType, sourceId, sourcePath, provenance, trustClass, role, string.Empty, CustomLoopTraceContentHash.Compute(string.Empty), 0, 0, false, null, "Source absent in test fixture.", Timestamp);
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
