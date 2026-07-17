using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed class CustomLoopAdmissionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        var definitions = new FakeDefinitionStore();
        var runs = new FakeRunStore();
        var audit = new RecordingAuditLog();

        Assert.Throws<ArgumentNullException>(() => new CustomLoopAdmissionService(null!, runs, audit));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopAdmissionService(definitions, null!, audit));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopAdmissionService(definitions, runs, null!));
    }

    [Fact]
    public async Task Admit_rejects_a_missing_request()
    {
        var service = Service(new FakeDefinitionStore(), new FakeRunStore());

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.AdmitAsync(null!));
    }

    [Fact]
    public async Task Envelope_validation_aggregates_server_boundary_errors_without_reading_state()
    {
        var definitions = new FakeDefinitionStore();
        var runs = new FakeRunStore();
        var request = Request(Definition()) with
        {
            LoopId = "..",
            ExpectedDefinitionVersion = 0,
            ExpectedDefinitionHash = new string('A', CustomLoopLimits.Sha256HexCharacters),
            OperationId = "bad/operation",
            Actor = " ",
            Surface = "Web UI",
            CurrentRoleId = "bad/role",
            InvocationPrompt = new string('x', CustomLoopLimits.MaxPresetPromptCharacters + 1),
            ModelSnapshot = null!,
            ContextSnapshot = null!
        };
        var audit = new RecordingAuditLog();

        var result = await Service(definitions, runs, audit).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, result.Status);
        Assert.False(result.IsAdmitted);
        Assert.Equal(
            [
                "invalid_loop_id",
                "invalid_operation_id",
                "invalid_current_role",
                "invalid_expected_version",
                "invalid_expected_hash",
                "actor_required",
                "invalid_surface",
                "model_snapshot_required",
                "context_snapshot_required",
                "invocation_prompt_too_long"
            ],
            result.ValidationErrors.Select(error => error.Code));
        Assert.Equal("The custom-loop invocation is invalid.", result.Detail);
        Assert.Equal(0, definitions.GetCallCount);
        Assert.Equal(0, runs.OperationLookupCallCount);
        Assert.Equal(0, runs.CreateCallCount);
        var auditEvent = AssertAdmissionAudit(audit, "invalid", AuditSchema.Outcomes.Rejected);
        Assert.Equal("embodysense.unknown", auditEvent.Actor);
        Assert.Null(auditEvent.Metadata["loop_id"]);
        Assert.Null(auditEvent.Metadata["operation_id"]);
        Assert.Contains("actor_required", Assert.IsType<string>(auditEvent.Metadata["validation_codes"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-web")]
    [InlineData("web-")]
    [InlineData("WEB")]
    public async Task Invalid_runtime_surfaces_never_reach_persistence(string surface)
    {
        var definition = Definition();
        var runs = new FakeRunStore();

        var result = await Service(new FakeDefinitionStore(definition), runs).AdmitAsync(Request(definition) with { Surface = surface });

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, result.Status);
        Assert.Equal(0, runs.CreateCallCount);
        Assert.Contains(result.ValidationErrors, error => error.Field == "surface");
    }

    [Fact]
    public async Task Operation_lookup_failure_is_fail_closed_before_definition_read()
    {
        var definition = Definition();
        var definitions = new FakeDefinitionStore(definition);
        var runs = new FakeRunStore { OperationLookupException = new IOException("broken operation index") };

        var result = await Service(definitions, runs).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, result.Status);
        Assert.Contains(nameof(IOException), result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, definitions.GetCallCount);
        Assert.Equal(0, runs.CreateCallCount);
    }

    [Fact]
    public async Task Definition_not_found_and_read_failure_are_distinct_fail_closed_results()
    {
        var absentAudit = new RecordingAuditLog();
        var absent = await Service(new FakeDefinitionStore(), new FakeRunStore(), absentAudit).AdmitAsync(Request(Definition()));
        var brokenDefinitions = new FakeDefinitionStore { GetException = new FormatException("corrupt definition") };
        var unreadableAudit = new RecordingAuditLog();
        var unreadable = await Service(brokenDefinitions, new FakeRunStore(), unreadableAudit).AdmitAsync(Request(Definition()));

        Assert.Equal(CustomLoopAdmissionStatus.NotFound, absent.Status);
        Assert.Contains("does not exist", absent.Detail, StringComparison.Ordinal);
        Assert.Equal(CustomLoopAdmissionStatus.Invalid, unreadable.Status);
        Assert.Contains(nameof(FormatException), unreadable.Detail, StringComparison.Ordinal);
        AssertAdmissionAudit(absentAudit, "not_found", AuditSchema.Outcomes.NotFound);
        AssertAdmissionAudit(unreadableAudit, "invalid", AuditSchema.Outcomes.Rejected);
    }

    [Fact]
    public async Task Definition_binding_requires_valid_content_current_role_prototype_defaults_version_and_hash()
    {
        var prototype = CustomLoopContextDefaults.CreatePrototypeDefaults();
        var changedDefaults = prototype with
        {
            Inference = prototype.Inference with
            {
                ContextIn = prototype.Inference.ContextIn with { IncludeRoleContext = false }
            }
        };
        var definition = Rehash(Definition() with
        {
            DisplayName = string.Empty,
            ContextDefaults = changedDefaults
        });
        var request = Request(definition) with
        {
            CurrentRoleId = "role-other",
            ExpectedDefinitionVersion = definition.DefinitionVersion + 1,
            ExpectedDefinitionHash = new string('0', CustomLoopLimits.Sha256HexCharacters)
        };
        var runs = new FakeRunStore();

        var result = await Service(new FakeDefinitionStore(definition), runs).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "role_binding_mismatch");
        Assert.Contains(result.ValidationErrors, error => error.Code == "server_context_defaults_changed");
        Assert.Contains(result.ValidationErrors, error => error.Code == "definition_conflict");
        Assert.Contains(result.ValidationErrors, error => error.Field == "displayName");
        Assert.Equal(0, runs.CreateCallCount);
    }

    [Fact]
    public async Task Invocation_trigger_requires_a_prompt_and_normalizes_it_to_nfc()
    {
        var definition = Definition();
        var missingRuns = new FakeRunStore();
        var missing = await Service(new FakeDefinitionStore(definition), missingRuns).AdmitAsync(Request(definition) with { InvocationPrompt = " \t" });

        const string decomposed = "Cafe\u0301 request";
        var acceptedRuns = new FakeRunStore();
        var accepted = await Service(new FakeDefinitionStore(definition), acceptedRuns).AdmitAsync(Request(definition) with { InvocationPrompt = decomposed });

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, missing.Status);
        Assert.Contains(missing.ValidationErrors, error => error.Code == "invocation_prompt_required");
        Assert.Equal(0, missingRuns.CreateCallCount);
        Assert.Equal(CustomLoopAdmissionStatus.Admitted, accepted.Status);
        Assert.Equal("Café request", accepted.Run?.TriggerPrompt);
        Assert.True(accepted.Run!.TriggerPrompt.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public async Task Preset_trigger_uses_only_the_canonical_preset()
    {
        var definition = Definition(CustomLoopTriggerPromptSource.Preset, presetPrompt: "Preset work");
        var accepted = await Service(new FakeDefinitionStore(definition), new FakeRunStore()).AdmitAsync(Request(definition) with { InvocationPrompt = null });
        var rejectedRuns = new FakeRunStore();
        var rejected = await Service(new FakeDefinitionStore(definition), rejectedRuns).AdmitAsync(Request(definition) with { InvocationPrompt = "extra" });

        Assert.Equal(CustomLoopAdmissionStatus.Admitted, accepted.Status);
        Assert.Equal("Preset work", accepted.Run?.TriggerPrompt);
        Assert.Equal(CustomLoopAdmissionStatus.Invalid, rejected.Status);
        Assert.Contains(rejected.ValidationErrors, error => error.Code == "invocation_prompt_not_allowed");
        Assert.Equal(0, rejectedRuns.CreateCallCount);
    }

    [Fact]
    public async Task No_prompt_trigger_admits_an_empty_prompt_and_rejects_supplied_content()
    {
        var definition = Definition(CustomLoopTriggerPromptSource.None);
        var accepted = await Service(new FakeDefinitionStore(definition), new FakeRunStore()).AdmitAsync(Request(definition) with { InvocationPrompt = null });
        var rejectedRuns = new FakeRunStore();
        var rejected = await Service(new FakeDefinitionStore(definition), rejectedRuns).AdmitAsync(Request(definition) with { InvocationPrompt = "do work" });

        Assert.Equal(CustomLoopAdmissionStatus.Admitted, accepted.Status);
        Assert.Equal(string.Empty, accepted.Run?.TriggerPrompt);
        Assert.Equal(CustomLoopAdmissionStatus.Invalid, rejected.Status);
        Assert.Contains(rejected.ValidationErrors, error => error.Code == "invocation_prompt_not_allowed");
        Assert.Equal(0, rejectedRuns.CreateCallCount);
    }

    [Fact]
    public async Task Unsupported_trigger_source_is_rejected_before_persistence()
    {
        var definition = Definition() with
        {
            TriggerPolicy = new CustomLoopTriggerPolicy((CustomLoopTriggerPromptSource)99, string.Empty, false)
        };
        definition = Rehash(definition);
        var runs = new FakeRunStore();

        var result = await Service(new FakeDefinitionStore(definition), runs).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "unsupported_trigger_source");
        Assert.Equal(0, runs.CreateCallCount);
    }

    [Fact]
    public async Task Context_manifest_is_bound_to_exact_unmodified_message_content()
    {
        const string decomposed = "role Cafe\u0301";
        var definition = Definition();
        var exact = Snapshot(directoryRoleContent: decomposed);
        var exactResult = await Service(new FakeDefinitionStore(definition), new FakeRunStore()).AdmitAsync(Request(definition) with { ContextSnapshot = exact });

        var tampered = exact with
        {
            SourceManifest = [exact.SourceManifest[0] with { Content = "role Café" }, .. exact.SourceManifest.Skip(1)]
        };
        var tamperedRuns = new FakeRunStore();
        var tamperedResult = await Service(new FakeDefinitionStore(definition), tamperedRuns).AdmitAsync(Request(definition) with { ContextSnapshot = tampered });

        Assert.Equal(CustomLoopAdmissionStatus.Admitted, exactResult.Status);
        Assert.Equal(decomposed, exactResult.Run?.ContextSnapshot.DirectoryRoleMessages[0].Content);
        Assert.Equal(CustomLoopAdmissionStatus.Invalid, tamperedResult.Status);
        Assert.Contains(tamperedResult.ValidationErrors, error => error.Code == "context_manifest_mismatch");
        Assert.Equal(0, tamperedRuns.CreateCallCount);
    }

    [Fact]
    public async Task Conversation_history_requires_both_trigger_admission_and_a_server_binding()
    {
        CustomLoopMessageSnapshot[] conversationMessages = [new CustomLoopMessageSnapshot(LlmMessageRole.User, "Earlier question")];
        var snapshot = Snapshot(invokingConversationMessages: conversationMessages);

        var excludedDefinition = Definition(includeInvokingConversation: false);
        var excluded = await Service(new FakeDefinitionStore(excludedDefinition), new FakeRunStore()).AdmitAsync(Request(excludedDefinition) with
        {
            InvokingConversation = Conversation(),
            ContextSnapshot = snapshot
        });

        var admittedDefinition = Definition(includeInvokingConversation: true);
        var unbound = await Service(new FakeDefinitionStore(admittedDefinition), new FakeRunStore()).AdmitAsync(Request(admittedDefinition) with
        {
            InvokingConversation = null,
            ContextSnapshot = snapshot
        });

        Assert.Contains(excluded.ValidationErrors, error => error.Code == "conversation_not_admitted");
        Assert.Contains(unbound.ValidationErrors, error => error.Code == "conversation_binding_required");
    }

    [Fact]
    public async Task Admitted_conversation_binding_and_exact_history_are_pinned_on_the_run()
    {
        var definition = Definition(includeInvokingConversation: true);
        var conversation = Conversation();
        var snapshot = Snapshot(invokingConversationMessages:
        [
            new CustomLoopMessageSnapshot(LlmMessageRole.User, "Earlier question"),
            new CustomLoopMessageSnapshot(LlmMessageRole.Assistant, "Earlier answer")
        ]);
        var request = Request(definition) with { InvokingConversation = conversation, ContextSnapshot = snapshot };

        var result = await Service(new FakeDefinitionStore(definition), new FakeRunStore()).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.Admitted, result.Status);
        Assert.Equal(conversation, result.Run?.InvokingConversation);
        Assert.Same(snapshot, result.Run?.ContextSnapshot);
        Assert.True(CustomLoopContextSnapshotHash.Matches(result.Run!.ContextSnapshot));
    }

    [Fact]
    public async Task Successful_admission_persists_a_valid_hash_bound_record_before_metadata_only_audit()
    {
        const string secretPrompt = "private invocation prompt";
        const string secretRoleContext = "private role context";
        var definition = Rehash(Definition() with { ToolAssignments = [CustomLoopToolAssignment.Read] });
        var context = Snapshot(secretRoleContext);
        var request = Request(definition) with { InvocationPrompt = secretPrompt, ContextSnapshot = context };
        var runs = new FakeRunStore();
        var audit = new RecordingAuditLog();
        var identity = new QueueIdentityGenerator(["run-admitted"], ["event-admitted", "event-audit-complete"]);

        var result = await Service(new FakeDefinitionStore(definition), runs, audit, identity).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.Admitted, result.Status);
        Assert.True(result.IsAdmitted);
        Assert.Same(runs.LastUpdated, result.Run);
        var run = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal("run-admitted", run.Id);
        Assert.Equal(2, run.LifecycleVersion);
        Assert.Equal(CustomLoopRunStatus.Admitted, run.Status);
        Assert.Equal(Now, run.CreatedAtUtc);
        Assert.Equal(Now, run.UpdatedAtUtc);
        Assert.Null(run.CompletedAtUtc);
        Assert.Equal(request.OperationId, run.AdmissionOperationId);
        Assert.Equal(definition, run.AdmittedDefinition);
        Assert.Equal(request.ModelSnapshot, run.ModelSnapshot);
        Assert.Equal(secretPrompt, run.TriggerPrompt);
        Assert.True(CustomLoopAdmissionRequestHash.Matches(run));
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(run).Errors));
        Assert.Equal(2, run.Events.Length);
        var admittedEvent = run.Events[0];
        Assert.Equal("event-admitted", admittedEvent.EventId);
        Assert.Equal(CustomLoopRunEventKind.Admitted, admittedEvent.Kind);
        Assert.Equal(request.ModelSnapshot.Provider, admittedEvent.Provider);
        Assert.Equal(request.ModelSnapshot.Model, admittedEvent.Model);
        Assert.Equal("event-audit-complete", run.Events[1].EventId);
        Assert.Equal(CustomLoopRunEventKind.AdmissionAuditCompleted, run.Events[1].Kind);
        Assert.True(CustomLoopRunValidator.HasCompleteAdmissionAudit(run));
        Assert.Equal(1, runs.CreateCallCount);
        Assert.Equal(1, runs.UpdateCallCount);

        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal("actor-user", auditEvent.Actor);
        Assert.Equal(AuditSchema.Actions.LoopRunAdmission, auditEvent.Action);
        Assert.Equal(AuditSchema.Outcomes.Succeeded, auditEvent.Outcome);
        Assert.Equal(run.Id, auditEvent.Target);
        Assert.Equal(run.Id, auditEvent.Metadata["run_id"]);
        Assert.Equal("admitted", auditEvent.Metadata["admission_status"]);
        Assert.Equal(nameof(CustomLoopToolAssignment.Read), auditEvent.Metadata["effective_tool_assignments"]);
        Assert.DoesNotContain(auditEvent.Metadata.Keys, key => key.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(auditEvent.Metadata.Keys, key => key.Contains("context", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(auditEvent.Metadata.Keys, key => key.Contains("output", StringComparison.OrdinalIgnoreCase));
        var serializedAudit = JsonSerializer.Serialize(auditEvent);
        Assert.DoesNotContain(secretPrompt, serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(secretRoleContext, serializedAudit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_operation_replay_returns_the_original_run_and_audits_the_idempotent_outcome()
    {
        var definition = Definition(includeInvokingConversation: true);
        var request = Request(definition) with
        {
            InvokingConversation = Conversation(),
            ContextSnapshot = Snapshot(invokingConversationMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.User, "history")])
        };
        var existing = ExistingRun(definition, request);
        var definitions = new FakeDefinitionStore(definition);
        var runs = new FakeRunStore { OperationReplay = existing };
        var audit = new RecordingAuditLog();

        var result = await Service(definitions, runs, audit).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.Replayed, result.Status);
        Assert.True(result.IsAdmitted);
        Assert.Same(existing, result.Run);
        Assert.Equal(0, definitions.GetCallCount);
        Assert.Equal(0, runs.CreateCallCount);
        var auditEvent = AssertAdmissionAudit(audit, "replayed", AuditSchema.Outcomes.Succeeded);
        Assert.Equal(existing.Id, auditEvent.Target);
        Assert.Equal(existing.Id, auditEvent.Metadata["run_id"]);
    }

    [Fact]
    public async Task Replay_compares_exact_message_values_instead_of_array_identity()
    {
        var definition = Definition(includeInvokingConversation: true);
        var original = Request(definition) with
        {
            InvokingConversation = Conversation(),
            ContextSnapshot = Snapshot(invokingConversationMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.User, "history")])
        };
        var existing = ExistingRun(definition, original);
        var clonedContext = original.ContextSnapshot with
        {
            SourceManifest = original.ContextSnapshot.SourceManifest.ToArray()
        };
        var sameValues = original with { ContextSnapshot = clonedContext };

        var replay = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = existing }).AdmitAsync(sameValues);

        var alteredMessagesWithReusedManifest = clonedContext with
        {
            SourceManifest = [clonedContext.SourceManifest[0] with { Content = "different role content" }, .. clonedContext.SourceManifest.Skip(1)]
        };
        var conflict = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = existing }).AdmitAsync(original with { ContextSnapshot = alteredMessagesWithReusedManifest });

        Assert.Equal(CustomLoopAdmissionStatus.Replayed, replay.Status);
        Assert.Equal(CustomLoopAdmissionStatus.Conflict, conflict.Status);
    }

    [Fact]
    public async Task Replay_treats_canonically_equivalent_nfc_invocation_text_as_the_same_request()
    {
        var definition = Definition();
        var admittedRequest = Request(definition) with { InvocationPrompt = "Café" };
        var existing = ExistingRun(definition, admittedRequest);
        var replayRequest = admittedRequest with { InvocationPrompt = "Cafe\u0301" };

        var result = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = existing }).AdmitAsync(replayRequest);

        Assert.Equal(CustomLoopAdmissionStatus.Replayed, result.Status);
        Assert.Same(existing, result.Run);
    }

    [Fact]
    public async Task Operation_reuse_conflicts_when_any_pinned_model_conversation_or_context_content_changes()
    {
        var definition = Definition(includeInvokingConversation: true);
        var original = Request(definition) with
        {
            InvokingConversation = Conversation(),
            ContextSnapshot = Snapshot(invokingConversationMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.User, "history")])
        };
        var existing = ExistingRun(definition, original);
        var changedModel = original with { ModelSnapshot = original.ModelSnapshot with { Model = "different-model" } };
        var changedProvider = original with { ModelSnapshot = original.ModelSnapshot with { Provider = "different-provider" } };
        var changedConversation = original with { InvokingConversation = original.InvokingConversation! with { CapturedVersion = "version-2" } };
        var removedConversation = original with { InvokingConversation = null };
        var changedContext = original with { ContextSnapshot = Snapshot(invokingConversationMessages: [new CustomLoopMessageSnapshot(LlmMessageRole.User, "different history")]) };
        var changedContextCapture = original with { ContextSnapshot = CustomLoopContextSnapshotHash.Apply(original.ContextSnapshot with { CapturedAtUtc = original.ContextSnapshot.CapturedAtUtc.AddSeconds(-1) }) };

        foreach (var changed in new[] { changedModel, changedProvider, changedConversation, removedConversation, changedContext, changedContextCapture })
        {
            var result = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = existing }).AdmitAsync(changed);
            Assert.Equal(CustomLoopAdmissionStatus.Conflict, result.Status);
            Assert.Same(existing, result.Run);
        }

        var noConversationRequest = Request(definition);
        var noConversationRun = ExistingRun(definition, noConversationRequest);
        var newlyBoundConversation = noConversationRequest with { InvokingConversation = Conversation() };
        var conversationConflict = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = noConversationRun }).AdmitAsync(newlyBoundConversation);

        var missingMessageArray = original.ContextSnapshot with { SourceManifest = null! };
        var contextConflict = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = existing }).AdmitAsync(original with { ContextSnapshot = missingMessageArray });

        Assert.Equal(CustomLoopAdmissionStatus.Conflict, conversationConflict.Status);
        Assert.Equal(CustomLoopAdmissionStatus.Conflict, contextConflict.Status);
    }

    [Fact]
    public async Task Operation_reuse_conflicts_when_existing_authorized_request_coordinates_change()
    {
        var definition = Definition();
        var original = Request(definition);
        var existing = ExistingRun(definition, original);
        var changedRequests = new[]
        {
            original with { OperationId = "invoke-other" },
            original with { LoopId = "loop-other" },
            original with { ExpectedDefinitionVersion = original.ExpectedDefinitionVersion + 1 },
            original with { ExpectedDefinitionHash = new string('0', CustomLoopLimits.Sha256HexCharacters) },
            original with { CurrentRoleId = "role-other" },
            original with { Surface = "cli" },
            original with { InvocationPrompt = "different prompt" }
        };

        foreach (var changed in changedRequests)
        {
            var result = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = existing }).AdmitAsync(changed);
            Assert.Equal(CustomLoopAdmissionStatus.Conflict, result.Status);
        }
    }

    [Fact]
    public async Task A_malformed_replay_record_is_rejected_instead_of_returned()
    {
        var definition = Definition();
        var request = Request(definition);
        var malformed = ExistingRun(definition, request) with { AdmissionRequestHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };

        var result = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = malformed }).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, result.Status);
        Assert.False(result.IsAdmitted);
        Assert.Contains(result.ValidationErrors, error => error.Code == "admission_request_hash_mismatch");
    }

    [Fact]
    public async Task Store_already_created_replays_only_the_same_canonical_request()
    {
        var definition = Definition();
        var request = Request(definition);
        var existing = ExistingRun(definition, request);
        var sameRuns = new FakeRunStore { CreateResult = CustomLoopRunStoreResult.AlreadyCreated(existing) };
        var same = await Service(new FakeDefinitionStore(definition), sameRuns).AdmitAsync(request);

        var different = existing with { TriggerPrompt = "other" };
        different = CustomLoopAdmissionRequestHash.Apply(different);
        var conflictRuns = new FakeRunStore { CreateResult = CustomLoopRunStoreResult.AlreadyCreated(different) };
        var conflict = await Service(new FakeDefinitionStore(definition), conflictRuns).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.Replayed, same.Status);
        Assert.Same(existing, same.Run);
        Assert.Equal(CustomLoopAdmissionStatus.Conflict, conflict.Status);
        Assert.Same(different, conflict.Run);
    }

    [Fact]
    public async Task Run_store_admission_statuses_map_and_audit_without_dispatch()
    {
        var definition = Definition();
        var request = Request(definition);
        var existing = ExistingRun(definition, request);
        var cases = new[]
        {
            (CustomLoopRunStoreResult.OperationConflict(existing), CustomLoopAdmissionStatus.Conflict, "conflict", AuditSchema.Outcomes.Conflict),
            (CustomLoopRunStoreResult.NonterminalRunExists(existing), CustomLoopAdmissionStatus.NonterminalRunExists, "nonterminal_run_exists", AuditSchema.Outcomes.Conflict),
            (CustomLoopRunStoreResult.LimitExceeded(), CustomLoopAdmissionStatus.LimitExceeded, "limit_exceeded", AuditSchema.Outcomes.Rejected),
            (CustomLoopRunStoreResult.NotFound(), CustomLoopAdmissionStatus.Invalid, "invalid", AuditSchema.Outcomes.Rejected),
            (new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.Unknown, null, null), CustomLoopAdmissionStatus.Invalid, "invalid", AuditSchema.Outcomes.Rejected),
            (new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.AlreadyCreated, null, null), CustomLoopAdmissionStatus.Invalid, "invalid", AuditSchema.Outcomes.Rejected)
        };

        foreach (var (storeResult, expected, auditedStatus, auditedOutcome) in cases)
        {
            var audit = new RecordingAuditLog();
            var runs = new FakeRunStore { CreateResult = storeResult };
            var result = await Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(request);

            Assert.Equal(expected, result.Status);
            var auditEvent = AssertAdmissionAudit(audit, auditedStatus, auditedOutcome);
            Assert.Equal(request.OperationId, auditEvent.Metadata["operation_id"]);
            Assert.Equal(string.Empty, auditEvent.Metadata["effective_tool_assignments"]);
            Assert.Equal(0, runs.UpdateCallCount);
        }
    }

    [Fact]
    public async Task Created_status_without_a_replacement_record_uses_the_exact_candidate()
    {
        var definition = Definition();
        var runs = new FakeRunStore { CreateResult = new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.Created, null, null) };

        var result = await Service(new FakeDefinitionStore(definition), runs).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.Admitted, result.Status);
        Assert.Same(runs.LastUpdated, result.Run);
        Assert.True(CustomLoopRunValidator.HasCompleteAdmissionAudit(result.Run));
    }

    [Fact]
    public async Task Create_exception_is_rejected_and_audited_without_integrity_update()
    {
        var definition = Definition();
        var runs = new FakeRunStore { CreateException = new IOException("trace unavailable") };
        var audit = new RecordingAuditLog();

        var result = await Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.Invalid, result.Status);
        Assert.Contains(nameof(IOException), result.Detail, StringComparison.Ordinal);
        AssertAdmissionAudit(audit, "invalid", AuditSchema.Outcomes.Rejected);
        Assert.Equal(0, runs.UpdateCallCount);
    }

    [Fact]
    public async Task Audit_failure_terminalizes_the_persisted_run_before_returning_no_dispatch_result()
    {
        var definition = Definition();
        var runs = new FakeRunStore();
        var audit = new RecordingAuditLog { AppendException = new IOException("audit unavailable") };
        var identity = new QueueIdentityGenerator(["run-admitted"], ["event-admitted", "event-integrity-failure"]);

        var result = await Service(new FakeDefinitionStore(definition), runs, audit, identity).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.False(result.IsAdmitted);
        var failed = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal(CustomLoopRunStatus.Failed, failed.Status);
        Assert.Equal(2, failed.LifecycleVersion);
        Assert.Equal(Now, failed.CompletedAtUtc);
        Assert.Equal("admission_audit_failed", failed.FailureCode);
        Assert.Contains(nameof(IOException), failed.FailureDetail, StringComparison.Ordinal);
        Assert.Equal("event-integrity-failure", failed.Events[^1].EventId);
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, failed.Events[^1].Kind);
        Assert.Same(failed, runs.LastUpdated);
        Assert.Equal(1, runs.UpdateCallCount);
        Assert.True(CustomLoopRunValidator.ValidateUpdate(runs.LastCreated, failed).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(runs.LastCreated, failed).Errors));
    }

    [Fact]
    public async Task Admission_audit_marker_failure_terminalizes_when_possible_and_never_reports_admission()
    {
        var definition = Definition();
        var runs = new FakeRunStore
        {
            UpdateResultFactory = (candidate, call) => call == 1 ? CustomLoopRunStoreResult.NotFound() : CustomLoopRunStoreResult.Updated(candidate)
        };

        var result = await Service(new FakeDefinitionStore(definition), runs).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.False(result.IsAdmitted);
        Assert.Equal(CustomLoopRunStatus.Failed, result.Run?.Status);
        Assert.Equal("admission_audit_failed", result.Run?.FailureCode);
        Assert.Equal(2, runs.UpdateCallCount);
        Assert.Equal(CustomLoopRunEventKind.AdmissionAuditCompleted, runs.UpdatedCandidates[0].Events[^1].Kind);
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, runs.UpdatedCandidates[1].Events[^1].Kind);
    }

    [Fact]
    public async Task Admission_audit_marker_and_terminalization_double_failure_leave_an_unmarked_artifact_that_cannot_claim_admission()
    {
        var definition = Definition();
        var runs = new FakeRunStore { UpdateResult = CustomLoopRunStoreResult.NotFound() };

        var result = await Service(new FakeDefinitionStore(definition), runs).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.False(result.IsAdmitted);
        Assert.Same(runs.LastCreated, result.Run);
        Assert.Equal(CustomLoopRunStatus.Admitted, result.Run?.Status);
        Assert.False(CustomLoopRunValidator.HasCompleteAdmissionAudit(result.Run));
        Assert.Equal(2, runs.UpdateCallCount);
    }

    [Fact]
    public async Task Audit_cancellation_after_durable_admission_terminalizes_instead_of_propagating()
    {
        var definition = Definition();
        var runs = new FakeRunStore();
        var audit = new RecordingAuditLog { AppendException = new OperationCanceledException("audit cancelled") };

        var result = await Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, result.Run?.Status);
        Assert.Equal("admission_audit_failed", result.Run?.FailureCode);
        Assert.Contains(nameof(OperationCanceledException), result.Run?.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, runs.UpdateCallCount);
    }

    [Fact]
    public async Task Caller_cancellation_after_durable_create_cannot_cancel_the_bounded_admission_audit()
    {
        var definition = Definition();
        using var callerCancellation = new CancellationTokenSource();
        var runs = new FakeRunStore
        {
            CreateResultFactory = candidate =>
            {
                callerCancellation.Cancel();
                return CustomLoopRunStoreResult.Created(candidate);
            }
        };
        var audit = new RecordingAuditLog();

        var result = await Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(Request(definition), callerCancellation.Token);

        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.Equal(CustomLoopAdmissionStatus.Admitted, result.Status);
        Assert.Single(audit.Events);
        var auditToken = Assert.Single(audit.AppendTokens);
        Assert.True(auditToken.CanBeCanceled);
        Assert.False(auditToken.IsCancellationRequested);
        Assert.Equal(1, runs.UpdateCallCount);
        var markerToken = Assert.Single(runs.UpdateTokens);
        Assert.True(markerToken.CanBeCanceled);
        Assert.False(markerToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Rejection_audit_failure_returns_audit_unavailable_without_persistence()
    {
        var definition = Definition();
        var runs = new FakeRunStore();
        var audit = new RecordingAuditLog { AppendException = new IOException("audit unavailable") };

        var result = await Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(Request(definition) with { Surface = "INVALID" });

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.Null(result.Run);
        Assert.False(result.IsAdmitted);
        Assert.Equal(0, runs.OperationLookupCallCount);
        Assert.Equal(0, runs.CreateCallCount);
        Assert.Equal(1, audit.AppendAttemptCount);
    }

    [Fact]
    public async Task Replay_audit_failure_returns_audit_unavailable_and_preserves_the_original_run()
    {
        var definition = Definition();
        var request = Request(definition);
        var existing = ExistingRun(definition, request);
        var runs = new FakeRunStore { OperationReplay = existing };
        var audit = new RecordingAuditLog { AppendException = new OperationCanceledException("audit cancelled") };

        var result = await Service(new FakeDefinitionStore(), runs, audit).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.Same(existing, result.Run);
        Assert.False(result.IsAdmitted);
        Assert.Equal(0, runs.CreateCallCount);
        Assert.Equal(0, runs.UpdateCallCount);
        Assert.Equal(1, audit.AppendAttemptCount);
    }

    [Fact]
    public async Task Replay_of_an_integrity_incomplete_admission_remains_audit_unavailable()
    {
        var definition = Definition();
        var request = Request(definition);
        var marked = ExistingRun(definition, request);
        var incomplete = marked with { LifecycleVersion = 1, Events = [marked.Events[0]] };
        var audit = new RecordingAuditLog();

        var result = await Service(new FakeDefinitionStore(), new FakeRunStore { OperationReplay = incomplete }, audit).AdmitAsync(request);

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.False(result.IsAdmitted);
        Assert.Same(incomplete, result.Run);
        AssertAdmissionAudit(audit, "audit_unavailable", AuditSchema.Outcomes.Rejected);
    }

    [Fact]
    public async Task Audit_integrity_update_failure_keeps_the_original_admitted_evidence_but_never_reports_admission()
    {
        var definition = Definition();
        var runs = new FakeRunStore { UpdateException = new IOException("trace update unavailable") };
        var audit = new RecordingAuditLog { AppendException = new IOException("audit unavailable") };

        var result = await Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.False(result.IsAdmitted);
        Assert.Same(runs.LastCreated, result.Run);
        Assert.Equal(CustomLoopRunStatus.Admitted, result.Run?.Status);
        Assert.Equal(1, runs.UpdateCallCount);
    }

    [Fact]
    public async Task Audit_integrity_uses_the_persisted_timestamp_and_handles_an_update_without_a_returned_record()
    {
        var definition = Definition();
        var future = Now.AddMinutes(1);
        var runs = new FakeRunStore
        {
            CreateResultFactory = candidate => CustomLoopRunStoreResult.Created(candidate with { UpdatedAtUtc = future }),
            UpdateResult = CustomLoopRunStoreResult.NotFound()
        };
        var audit = new RecordingAuditLog { AppendException = new IOException("audit unavailable") };

        var result = await Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(Request(definition));

        Assert.Equal(CustomLoopAdmissionStatus.AuditUnavailable, result.Status);
        Assert.Equal(CustomLoopRunStatus.Admitted, result.Run?.Status);
        Assert.Equal(future, result.Run?.UpdatedAtUtc);
        Assert.Equal(future, runs.LastUpdated?.UpdatedAtUtc);
        Assert.Equal(future, runs.LastUpdated?.CompletedAtUtc);
    }

    [Theory]
    [InlineData("operation-lookup")]
    [InlineData("definition-read")]
    [InlineData("create")]
    public async Task Cancellation_before_durable_admission_propagates(string boundary)
    {
        var definition = Definition();
        var cancellation = new OperationCanceledException("cancelled");
        var definitions = new FakeDefinitionStore(definition)
        {
            GetException = boundary == "definition-read" ? cancellation : null
        };
        var runs = new FakeRunStore
        {
            OperationLookupException = boundary == "operation-lookup" ? cancellation : null,
            CreateException = boundary == "create" ? cancellation : null
        };
        var audit = new RecordingAuditLog();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(definitions, runs, audit).AdmitAsync(Request(definition)));

        Assert.Equal(0, runs.UpdateCallCount);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_pre_persistence_rejection_audit_may_propagate()
    {
        var definition = Definition();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var audit = new RecordingAuditLog { AppendException = new OperationCanceledException(cancellation.Token) };
        var runs = new FakeRunStore();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(new FakeDefinitionStore(definition), runs, audit).AdmitAsync(Request(definition) with { Surface = "INVALID" }, cancellation.Token));

        Assert.Equal(0, runs.OperationLookupCallCount);
        Assert.Equal(0, runs.CreateCallCount);
        Assert.Equal(1, audit.AppendAttemptCount);
    }

    [Fact]
    public void Default_identity_generator_issues_distinct_safe_run_and_event_ids()
    {
        var generator = new CustomLoopRunIdentityGenerator();

        var runOne = generator.NewRunId();
        var runTwo = generator.NewRunId();
        var eventOne = generator.NewEventId();
        var eventTwo = generator.NewEventId();

        Assert.StartsWith("run-", runOne, StringComparison.Ordinal);
        Assert.StartsWith("event-", eventOne, StringComparison.Ordinal);
        Assert.NotEqual(runOne, runTwo);
        Assert.NotEqual(eventOne, eventTwo);
        Assert.True(CustomLoopArtifactIdentifier.IsValid(runOne));
        Assert.True(CustomLoopArtifactIdentifier.IsValid(runTwo));
        Assert.True(CustomLoopArtifactIdentifier.IsValid(eventOne));
        Assert.True(CustomLoopArtifactIdentifier.IsValid(eventTwo));
    }

    private static CustomLoopAdmissionService Service(FakeDefinitionStore definitions, FakeRunStore runs, RecordingAuditLog? audit = null, ICustomLoopRunIdentityGenerator? identity = null)
    {
        return new CustomLoopAdmissionService(definitions, runs, audit ?? new RecordingAuditLog(), identity ?? new QueueIdentityGenerator(["run-admitted"], ["event-admitted", "event-audit-complete", "event-integrity-failure"]), new FixedTimeProvider(Now));
    }

    private static AuditEvent AssertAdmissionAudit(RecordingAuditLog audit, string status, string outcome)
    {
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(AuditSchema.Actions.LoopRunAdmission, auditEvent.Action);
        Assert.Equal(outcome, auditEvent.Outcome);
        Assert.Equal(status, auditEvent.Metadata["admission_status"]);
        Assert.DoesNotContain(auditEvent.Metadata.Keys, key => key.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(auditEvent.Metadata.Keys, key => key.Contains("context", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(auditEvent.Metadata.Keys, key => key.Contains("output", StringComparison.OrdinalIgnoreCase));
        return auditEvent;
    }

    private static CustomLoopDefinition Definition(CustomLoopTriggerPromptSource promptSource = CustomLoopTriggerPromptSource.Invocation, string presetPrompt = "", bool includeInvokingConversation = false)
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-admission", "role-workspace", "step-admission", "create-loop", Now.AddHours(-1));
        return Rehash(seed with
        {
            DisplayName = "Admission loop",
            TriggerPolicy = new CustomLoopTriggerPolicy(promptSource, presetPrompt, includeInvokingConversation)
        });
    }

    private static CustomLoopDefinition Rehash(CustomLoopDefinition definition)
    {
        return CustomLoopDefinitionContentHash.Apply(definition with { ContentHash = string.Empty });
    }

    private static CustomLoopAdmissionRequest Request(CustomLoopDefinition definition)
    {
        return new CustomLoopAdmissionRequest(
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            "invoke-operation",
            "actor-user",
            "web",
            definition.RoleId,
            definition.TriggerPolicy.PromptSource == CustomLoopTriggerPromptSource.Invocation ? "Initial prompt" : null,
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            Snapshot());
    }

    private static CustomLoopContextSnapshot Snapshot(string directoryRoleContent = "Directory role context", CustomLoopMessageSnapshot[]? invokingConversationMessages = null)
    {
        var capturedAtUtc = Now.AddMinutes(-1);
        var manifest = new List<CustomLoopContextManifestSource>
        {
            IncludedSource(1, CustomLoopContextSource.RoleInstruction, "nearest-agents", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, directoryRoleContent, capturedAtUtc),
            OmittedSource(2, CustomLoopContextSource.RoleInstruction, "agent", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
            OmittedSource(3, CustomLoopContextSource.RoleInstruction, "soul", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
            OmittedSource(4, CustomLoopContextSource.RoleInstruction, "personality", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
            OmittedSource(5, CustomLoopContextSource.ContextualState, "context", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc),
            OmittedSource(6, CustomLoopContextSource.ContextualState, "memory", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc),
            OmittedSource(7, CustomLoopContextSource.ContextualState, "models", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc)
        };
        foreach (var (message, index) in (invokingConversationMessages ?? []).Select((message, index) => (message, index)))
        {
            manifest.Add(IncludedSource(8 + index, CustomLoopContextSource.InvokingConversation, $"invoking-conversation-{index + 1}", CustomLoopContextProvenance.LogicalConversation, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, message.Content, capturedAtUtc));
        }

        return CustomLoopContextSnapshotHash.Apply(new CustomLoopContextSnapshot(CustomLoopContextSnapshot.CurrentSchemaVersion, capturedAtUtc, manifest.ToArray(), string.Empty));
    }

    private static CustomLoopContextManifestSource IncludedSource(int order, CustomLoopContextSource source, string sourceId, CustomLoopContextProvenance provenance, CustomLoopContextTrustClass trustClass, LlmMessageRole role, string content, DateTimeOffset capturedAtUtc)
    {
        return new CustomLoopContextManifestSource(order, source, sourceId, TestSourcePath(sourceId), provenance, trustClass, role, content, CustomLoopTraceContentHash.Compute(content), content.Length, content.Length, false, null, null, capturedAtUtc);
    }

    private static CustomLoopContextManifestSource OmittedSource(int order, CustomLoopContextSource source, string sourceId, CustomLoopContextProvenance provenance, CustomLoopContextTrustClass trustClass, LlmMessageRole role, DateTimeOffset capturedAtUtc)
    {
        return new CustomLoopContextManifestSource(order, source, sourceId, TestSourcePath(sourceId), provenance, trustClass, role, string.Empty, CustomLoopTraceContentHash.Compute(string.Empty), 0, 0, false, null, "Source absent in test fixture.", capturedAtUtc);
    }

    private static string TestSourcePath(string sourceId)
    {
        return sourceId switch
        {
            "nearest-agents" => "test/AGENTS.md",
            "agent" => "test/.agent/AGENT.md",
            "soul" => "test/.agent/SOUL.md",
            "personality" => "test/.agent/PERSONALITY.md",
            "context" => "test/.agent/CONTEXT.md",
            "memory" => "test/.agent/MEMORY.md",
            "models" => "test/.agent/models.json",
            _ => $"test/{sourceId}"
        };
    }

    private static CustomLoopConversationReference Conversation()
    {
        return new CustomLoopConversationReference("conversation-bound", "version-1", Now.AddMinutes(-1));
    }

    private static CustomLoopRunRecord ExistingRun(CustomLoopDefinition definition, CustomLoopAdmissionRequest request)
    {
        var triggerPrompt = definition.TriggerPolicy.PromptSource switch
        {
            CustomLoopTriggerPromptSource.Invocation => request.InvocationPrompt?.Normalize(NormalizationForm.FormC) ?? string.Empty,
            CustomLoopTriggerPromptSource.Preset => definition.TriggerPolicy.PresetPrompt,
            _ => string.Empty
        };
        var admitted = new CustomLoopRunEvent(
            1,
            "event-existing",
            Now,
            CustomLoopRunEventKind.Admitted,
            null,
            null,
            null,
            "Run admitted.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            request.ModelSnapshot.Provider,
            request.ModelSnapshot.Model,
            null,
            null);
        var auditCompleted = new CustomLoopRunEvent(2, "event-existing-audit-complete", Now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-existing",
            definition.Id,
            2,
            CustomLoopRunStatus.Admitted,
            Now,
            Now,
            null,
            request.Surface,
            request.ModelSnapshot,
            request.OperationId,
            string.Empty,
            definition,
            triggerPrompt,
            request.InvokingConversation,
            request.ContextSnapshot,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admitted, auditCompleted],
            null,
            null,
            null);
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(run).Errors));
        return run;
    }

    private sealed class QueueIdentityGenerator(IEnumerable<string> runIds, IEnumerable<string> eventIds) : ICustomLoopRunIdentityGenerator
    {
        private readonly Queue<string> _runIds = new(runIds);
        private readonly Queue<string> _eventIds = new(eventIds);

        public string NewRunId()
        {
            return _runIds.Dequeue();
        }

        public string NewEventId()
        {
            return _eventIds.Dequeue();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public Exception? AppendException { get; init; }

        public List<AuditEvent> Events { get; } = [];

        public List<CancellationToken> AppendTokens { get; } = [];

        public int AppendAttemptCount { get; private set; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            AppendAttemptCount++;
            AppendTokens.Add(cancellationToken);
            if (AppendException is not null)
            {
                return Task.FromException(AppendException);
            }

            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
        }
    }

    private sealed class FakeDefinitionStore(params CustomLoopDefinition[] definitions) : ICustomLoopDefinitionStore
    {
        private readonly Dictionary<string, CustomLoopDefinition> _definitions = definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

        public Exception? GetException { get; init; }

        public int GetCallCount { get; private set; }

        public Task<CustomLoopDefinition?> GetAsync(string loopId, CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            if (GetException is not null)
            {
                return Task.FromException<CustomLoopDefinition?>(GetException);
            }

            _definitions.TryGetValue(loopId, out var definition);
            return Task.FromResult(definition);
        }

        public Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopCreateOperationLookupResult> GetCreateOperationAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopDefinition>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopOperationAuditMarkStatus> MarkOperationOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRunStore : ICustomLoopRunStore
    {
        public CustomLoopRunRecord? OperationReplay { get; init; }

        public Exception? OperationLookupException { get; init; }

        public CustomLoopRunStoreResult? CreateResult { get; init; }

        public Func<CustomLoopRunRecord, CustomLoopRunStoreResult>? CreateResultFactory { get; init; }

        public Exception? CreateException { get; init; }

        public CustomLoopRunStoreResult? UpdateResult { get; init; }

        public Func<CustomLoopRunRecord, int, CustomLoopRunStoreResult>? UpdateResultFactory { get; init; }

        public Exception? UpdateException { get; init; }

        public int OperationLookupCallCount { get; private set; }

        public int CreateCallCount { get; private set; }

        public int UpdateCallCount { get; private set; }

        public CustomLoopRunRecord? LastCreated { get; private set; }

        public CustomLoopRunRecord? LastUpdated { get; private set; }

        public List<CancellationToken> UpdateTokens { get; } = [];

        public List<CustomLoopRunRecord> UpdatedCandidates { get; } = [];

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
        {
            OperationLookupCallCount++;
            if (OperationLookupException is not null)
            {
                return Task.FromException<CustomLoopRunRecord?>(OperationLookupException);
            }

            return Task.FromResult(OperationReplay);
        }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            LastCreated = run;
            if (CreateException is not null)
            {
                return Task.FromException<CustomLoopRunStoreResult>(CreateException);
            }

            return Task.FromResult(CreateResultFactory?.Invoke(run) ?? CreateResult ?? CustomLoopRunStoreResult.Created(run));
        }

        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            LastUpdated = run;
            UpdateTokens.Add(cancellationToken);
            UpdatedCandidates.Add(run);
            if (UpdateException is not null)
            {
                return Task.FromException<CustomLoopRunStoreResult>(UpdateException);
            }

            return Task.FromResult(UpdateResultFactory?.Invoke(run, UpdateCallCount) ?? UpdateResult ?? CustomLoopRunStoreResult.Updated(run));
        }

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
