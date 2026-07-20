using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using static EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunValidationRules;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public static class CustomLoopRunValidator
{
    public static CustomLoopValidationResult Validate(CustomLoopRunRecord? run)
    {
        var errors = new List<CustomLoopValidationError>();
        if (run is null)
        {
            Add(errors, "run_required", "$", "Custom loop run is required.");
            return new CustomLoopValidationResult(errors);
        }

        ValidateIdentity(run, errors);
        ValidateTimestamps(run, errors);
        ValidateAdmission(run, errors);
        ValidateContextSnapshot(run.ContextSnapshot, run.UpdatedAtUtc, errors);
        ValidateExecutionClock(run, errors);
        ValidateEvents(run, errors);
        ValidateCheckpoint(run, errors);
        ValidateOutcome(run, errors);
        return new CustomLoopValidationResult(errors);
    }

    public static CustomLoopValidationResult ValidateForDispatch(CustomLoopRunRecord? run)
    {
        var errors = Validate(run).Errors.ToList();
        if (run is not null && !HasCompleteAdmissionAudit(run))
        {
            Add(errors, "admission_audit_incomplete", "events", "Provider dispatch requires the durable admission-audit completion marker.");
        }

        return new CustomLoopValidationResult(errors);
    }

    public static bool HasCompleteAdmissionAudit(CustomLoopRunRecord? run)
    {
        if (run?.Events is not { Length: >= 2 } events)
        {
            return false;
        }

        return events[0] is { Sequence: 1, Kind: CustomLoopRunEventKind.Admitted }
            && events[1] is { Sequence: 2, Kind: CustomLoopRunEventKind.AdmissionAuditCompleted }
            && events.Count(item => item is { Kind: CustomLoopRunEventKind.AdmissionAuditCompleted }) == 1;
    }

    public static CustomLoopValidationResult ValidateUpdate(CustomLoopRunRecord? current, CustomLoopRunRecord? candidate)
    {
        var errors = Validate(candidate).Errors.ToList();
        if (current is null)
        {
            Add(errors, "current_run_required", "$", "The current custom loop run is required for update validation.");
            return new CustomLoopValidationResult(errors);
        }

        if (candidate is null)
        {
            return new CustomLoopValidationResult(errors);
        }

        if (current.IsTerminal)
        {
            Add(errors, "terminal_run_immutable", "status", "Terminal custom loop runs are immutable.");
        }

        if (candidate.LifecycleVersion != checked(current.LifecycleVersion + 1))
        {
            Add(errors, "invalid_lifecycle_successor", "lifecycleVersion", "Updated lifecycle version must be exactly one greater than the persisted version.");
        }

        ValidateImmutableAdmission(current, candidate, errors);
        ValidateLifecycleTransition(current, candidate, errors);
        ValidateAppendOnlyEvents(current, candidate, errors);
        ValidateAppendedControlOwnership(current, candidate, errors);
        ValidateMonotonicCheckpoint(current, candidate, errors);
        ValidateMonotonicExecutionClock(current, candidate, errors);
        if (candidate.UpdatedAtUtc < current.UpdatedAtUtc)
        {
            Add(errors, "updated_timestamp_regressed", "updatedAtUtc", "Updated timestamp cannot move backward.");
        }

        return new CustomLoopValidationResult(errors);
    }

    public static CustomLoopValidationResult ValidateTerminalIntegrityWarningAppend(CustomLoopRunRecord? current, CustomLoopRunEvent? warning)
    {
        var errors = Validate(current).Errors.ToList();
        if (current is null)
        {
            Add(errors, "current_run_required", "$", "The current custom loop run is required for a terminal integrity-warning append.");
            return new CustomLoopValidationResult(errors);
        }

        if (warning is null)
        {
            Add(errors, "integrity_warning_required", "warning", "A terminal integrity-warning event is required.");
            return new CustomLoopValidationResult(errors);
        }

        if (!current.IsTerminal)
        {
            Add(errors, "terminal_run_required", "status", "Only a terminal custom loop run can receive the one post-terminal integrity warning.");
        }

        if (current.Events.LastOrDefault()?.Kind == CustomLoopRunEventKind.IntegrityWarning)
        {
            Add(errors, "terminal_integrity_warning_already_appended", "events", "A terminal run can receive at most one post-terminal integrity warning.");
        }
        else if (current.Events.LastOrDefault()?.Kind != CustomLoopRunEventKind.LifecycleChanged)
        {
            Add(errors, "terminal_lifecycle_boundary_required", "events", "The post-terminal integrity warning must immediately follow the terminal lifecycle event.");
        }

        if (warning.Kind != CustomLoopRunEventKind.IntegrityWarning)
        {
            Add(errors, "integrity_warning_kind_required", "warning.kind", "The post-terminal event must be an IntegrityWarning.");
        }

        if (warning.Sequence != current.Events.Length + 1L)
        {
            Add(errors, "invalid_integrity_warning_sequence", "warning.sequence", "The post-terminal integrity warning must be the next contiguous event.");
        }

        if (warning.Iteration is not null || warning.StepId is not null || warning.Attempt is not null || warning.ContextBlocks is not { Length: 0 }
            || warning.CanonicalOutput is not null || warning.OriginalOutputCharacterCount is not null || warning.CanonicalOutputTruncated is not null
            || warning.RetainedForLoopReasoning is not null || warning.PublishedToInvokingConversation is not null || warning.ConversationPublicationId is not null
            || warning.Provider is not null || warning.Model is not null || warning.ProviderResponseId is not null || warning.ExitDecision is not null
            || warning.ToolAuthority is not null || warning.ToolEvidence is not null || warning.TraceReservationUtf8Bytes is not null || warning.ControlExpectedLifecycleVersion is not null)
        {
            Add(errors, "invalid_terminal_integrity_warning", "warning", "The post-terminal integrity warning can carry only its sequence, id, timestamp, kind, detail, and an empty context-block list.");
        }

        var candidate = current with
        {
            LifecycleVersion = checked(current.LifecycleVersion + 1),
            UpdatedAtUtc = warning.TimestampUtc,
            Events = [.. current.Events, warning]
        };
        errors.AddRange(Validate(candidate).Errors);
        return new CustomLoopValidationResult(errors);
    }

    public static bool IsAllowedLifecycleTransition(CustomLoopRunStatus current, CustomLoopRunStatus next)
    {
        if (current == next)
        {
            return true;
        }

        return current switch
        {
            CustomLoopRunStatus.Admitted => next is CustomLoopRunStatus.Running or CustomLoopRunStatus.Paused or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.Running => next is CustomLoopRunStatus.PauseRequested or CustomLoopRunStatus.Paused or CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.PauseRequested => next is CustomLoopRunStatus.Paused or CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.Paused => next is CustomLoopRunStatus.Running or CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.CancelRequested => next is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview,
            _ => false
        };
    }

    private static void ValidateIdentity(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.SchemaVersion != CustomLoopRunRecord.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_run_schema", "schemaVersion", $"Run schema version must be {CustomLoopRunRecord.CurrentSchemaVersion}.");
        }

        ValidateArtifactId(run.Id, "id", errors);
        ValidateArtifactId(run.LoopId, "loopId", errors);
        if (run.LifecycleVersion < 1)
        {
            Add(errors, "invalid_lifecycle_version", "lifecycleVersion", "Lifecycle version must be at least 1.");
        }

        if (!Enum.IsDefined(run.Status) || run.Status == CustomLoopRunStatus.Unknown)
        {
            Add(errors, "unsupported_run_status", "status", "Run status must be a supported concrete lifecycle state.");
        }

        if (!IsRuntimeSurface(run.Surface))
        {
            Add(errors, "invalid_surface", "surface", "Surface must be a normalized lowercase runtime-surface identifier.");
        }

        if (!CustomLoopArtifactIdentifier.IsValid(run.AdmissionOperationId, CustomLoopLimits.MaxMutationOperationIdCharacters))
        {
            Add(errors, "invalid_admission_operation_id", "admissionOperationId", "Admission operation id must be a safe lowercase artifact identifier.");
        }

        ValidateActorText(run.AdmissionActor, "admissionActor", CustomLoopLimits.MaxTraceReferenceCharacters, errors);
        ValidateHash(run.AdmissionRequestHash, "admissionRequestHash", errors);
    }

    private static void ValidateTimestamps(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (!IsUtcTimestamp(run.CreatedAtUtc))
        {
            Add(errors, "invalid_created_timestamp", "createdAtUtc", "Created timestamp must be a non-default UTC value.");
        }

        if (!IsUtcTimestamp(run.UpdatedAtUtc))
        {
            Add(errors, "invalid_updated_timestamp", "updatedAtUtc", "Updated timestamp must be a non-default UTC value.");
        }

        if (run.CreatedAtUtc > run.UpdatedAtUtc)
        {
            Add(errors, "invalid_timestamp_order", "updatedAtUtc", "Updated timestamp cannot precede the created timestamp.");
        }

        if (run.IsTerminal)
        {
            if (run.CompletedAtUtc is not { } completedAt || !IsUtcTimestamp(completedAt) || completedAt < run.CreatedAtUtc || completedAt > run.UpdatedAtUtc)
            {
                Add(errors, "invalid_completed_timestamp", "completedAtUtc", "Terminal runs require a UTC completion timestamp between creation and the latest update.");
            }
        }
        else if (run.CompletedAtUtc is not null)
        {
            Add(errors, "unexpected_completed_timestamp", "completedAtUtc", "Nonterminal runs cannot have a completion timestamp.");
        }
    }

    private static void ValidateAdmission(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.ModelSnapshot is null)
        {
            Add(errors, "model_snapshot_required", "modelSnapshot", "A pinned provider/model snapshot is required.");
        }
        else
        {
            ValidateText(run.ModelSnapshot.Provider, "modelSnapshot.provider", CustomLoopLimits.MaxTraceReferenceCharacters, required: true, errors);
            ValidateOptionalText(run.ModelSnapshot.Model, "modelSnapshot.model", CustomLoopLimits.MaxTraceReferenceCharacters, errors);
        }

        if (run.AdmittedDefinition is null)
        {
            Add(errors, "admitted_definition_required", "admittedDefinition", "A canonical admitted definition snapshot is required.");
        }
        else
        {
            var definitionValidation = CustomLoopDefinitionValidator.Validate(run.AdmittedDefinition);
            foreach (var error in definitionValidation.Errors)
            {
                Add(errors, error.Code, $"admittedDefinition.{error.Field}", error.Message);
            }

            if (!string.Equals(run.LoopId, run.AdmittedDefinition.Id, StringComparison.Ordinal))
            {
                Add(errors, "admitted_loop_mismatch", "loopId", "Run loop id must match the admitted definition id.");
            }
        }

        ValidateText(run.TriggerPrompt, "triggerPrompt", CustomLoopLimits.MaxPresetPromptCharacters, required: false, errors);
        if (run.InvokingConversation is { } conversation)
        {
            ValidateArtifactId(conversation.ConversationId, "invokingConversation.conversationId", errors);
            ValidateText(conversation.CapturedVersion, "invokingConversation.capturedVersion", CustomLoopLimits.MaxTraceReferenceCharacters, required: true, errors);
            if (!IsUtcTimestamp(conversation.CapturedAtUtc) || conversation.CapturedAtUtc > run.UpdatedAtUtc)
            {
                Add(errors, "invalid_conversation_capture_timestamp", "invokingConversation.capturedAtUtc", "Conversation capture timestamp must be a non-default UTC value no later than the run update.");
            }
        }

        if (IsSha256(run.AdmissionRequestHash) && !CustomLoopAdmissionRequestHash.Matches(run))
        {
            Add(errors, "admission_request_hash_mismatch", "admissionRequestHash", "Admission request hash does not match the pinned definition, model, input, and context snapshot.");
        }
    }

    private static void ValidateExecutionClock(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.ExecutionClock is null)
        {
            Add(errors, "execution_clock_required", "executionClock", "A persisted execution clock is required.");
            return;
        }

        if (run.ExecutionClock.AccumulatedRunningMilliseconds < 0)
        {
            Add(errors, "execution_clock_out_of_range", "executionClock.accumulatedRunningMilliseconds", "Accumulated running time cannot be negative; the runtime deadline is evaluated separately against the persisted value.");
        }

        if (run.ExecutionClock.ActiveSinceUtc is { } activeSince && (!IsUtcTimestamp(activeSince) || activeSince < run.CreatedAtUtc || activeSince > run.UpdatedAtUtc))
        {
            Add(errors, "invalid_active_since_timestamp", "executionClock.activeSinceUtc", "Active-since timestamp must be a UTC value within the run timestamp range.");
        }

        if (run.Status is CustomLoopRunStatus.Admitted or CustomLoopRunStatus.Paused or CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview && run.ExecutionClock.ActiveSinceUtc is not null)
        {
            Add(errors, "unexpected_active_execution_clock", "executionClock.activeSinceUtc", "Admitted, safely paused, and terminal runs cannot retain an active execution-clock timestamp.");
        }

        if (run.Status is CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested && run.ExecutionClock.ActiveSinceUtc is null)
        {
            Add(errors, "active_execution_clock_required", "executionClock.activeSinceUtc", "Running and pause-requested runs require an active execution-clock timestamp.");
        }
    }

    private static void ValidateContextSnapshot(CustomLoopContextSnapshot? snapshot, DateTimeOffset updatedAtUtc, List<CustomLoopValidationError> errors)
    {
        if (snapshot is null)
        {
            Add(errors, "context_snapshot_required", "contextSnapshot", "An immutable context snapshot is required.");
            return;
        }

        if (snapshot.SchemaVersion != CustomLoopContextSnapshot.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_context_schema", "contextSnapshot.schemaVersion", $"Context snapshot schema version must be {CustomLoopContextSnapshot.CurrentSchemaVersion}.");
        }

        if (!IsUtcTimestamp(snapshot.CapturedAtUtc) || snapshot.CapturedAtUtc > updatedAtUtc)
        {
            Add(errors, "invalid_context_capture_timestamp", "contextSnapshot.capturedAtUtc", "Context capture timestamp must be a non-default UTC value no later than the run update.");
        }

        ValidateHash(snapshot.ManifestHash, "contextSnapshot.manifestHash", errors);
        if (IsSha256(snapshot.ManifestHash) && !CustomLoopContextSnapshotHash.Matches(snapshot))
        {
            Add(errors, "context_manifest_hash_mismatch", "contextSnapshot.manifestHash", "Context manifest hash does not match the exact typed admitted sources and metadata.");
        }

        ValidateContextManifest(snapshot, errors);
    }

    private static void ValidateContextManifest(CustomLoopContextSnapshot snapshot, List<CustomLoopValidationError> errors)
    {
        if (snapshot.SourceManifest is null)
        {
            Add(errors, "context_manifest_required", "contextSnapshot.sourceManifest", "Typed context source manifest is required.");
            return;
        }

        var expectedWorkspaceSources = new[]
        {
            (Id: "nearest-agents", PathSuffix: "AGENTS.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "agent", PathSuffix: ".agent/AGENT.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "soul", PathSuffix: ".agent/SOUL.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "personality", PathSuffix: ".agent/PERSONALITY.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "context", PathSuffix: ".agent/CONTEXT.md", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User),
            (Id: "memory", PathSuffix: ".agent/MEMORY.md", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User),
            (Id: "models", PathSuffix: ".agent/models.json", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User)
        };
        if (snapshot.SourceManifest.Length < expectedWorkspaceSources.Length)
        {
            Add(errors, "incomplete_workspace_context_manifest", "contextSnapshot.sourceManifest", "The manifest must record all seven designated workspace role/context sources, including explicit omissions.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var conversationCharacters = 0L;
        var includedConversationEntries = 0;
        var omittedConversationEntries = 0;
        for (var index = 0; index < snapshot.SourceManifest.Length; index++)
        {
            var source = snapshot.SourceManifest[index];
            var field = $"contextSnapshot.sourceManifest[{index}]";
            if (source is null)
            {
                Add(errors, "context_manifest_source_required", field, "Context manifest source cannot be null.");
                continue;
            }

            if (source.Order != index + 1)
            {
                Add(errors, "invalid_context_source_order", $"{field}.order", "Context manifest order must be contiguous and match persisted source order.");
            }

            if (!Enum.IsDefined(source.SourceType) || source.SourceType is CustomLoopContextSource.Unknown or CustomLoopContextSource.HarnessGovernance or CustomLoopContextSource.RunMetadata or CustomLoopContextSource.NodeInstruction or CustomLoopContextSource.TriggerPrompt or CustomLoopContextSource.EarlierRetainedOutput or CustomLoopContextSource.PreviousIterationResult)
            {
                Add(errors, "unsupported_manifest_source_type", $"{field}.sourceType", "Admission manifest may contain only role instruction, contextual state, and invoking-conversation sources.");
            }

            if (!Enum.IsDefined(source.Provenance) || source.Provenance == CustomLoopContextProvenance.Unknown)
            {
                Add(errors, "unsupported_context_provenance", $"{field}.provenance", "Context source provenance must be a supported concrete class.");
            }

            if (!Enum.IsDefined(source.TrustClass) || source.TrustClass == CustomLoopContextTrustClass.Unknown)
            {
                Add(errors, "unsupported_context_trust_class", $"{field}.trustClass", "Context source trust class must be explicit.");
            }

            if (!Enum.IsDefined(source.Role) || source.Role == LlmMessageRole.Unknown)
            {
                Add(errors, "unsupported_context_role", $"{field}.role", "Context source role must be a supported concrete value.");
            }

            ValidateText(source.SourceId, $"{field}.sourceId", CustomLoopLimits.MaxTraceReferenceCharacters, required: true, errors);
            ValidateText(source.SourcePath, $"{field}.sourcePath", CustomLoopLimits.MaxTraceReferenceCharacters, required: true, errors, requireNormalized: false);
            if (!string.IsNullOrEmpty(source.SourceId) && !sourceIds.Add(source.SourceId))
            {
                Add(errors, "duplicate_context_source_id", $"{field}.sourceId", "Context manifest source identities must be unique.");
            }

            ValidateOptionalText(source.TruncationReason, $"{field}.truncationReason", CustomLoopLimits.MaxRunDetailCharacters, errors);
            ValidateOptionalText(source.OmissionReason, $"{field}.omissionReason", CustomLoopLimits.MaxRunDetailCharacters, errors);
            ValidateText(source.Content, $"{field}.content", CustomLoopLimits.MaxLogicalProviderRequestCharacters, required: source.OmissionReason is null, errors, requireNormalized: false);
            ValidateContentHash(source.Content, source.ContentHash, $"{field}.contentHash", errors);
            ValidateContextManifestCounts(source, field, errors);
            if (!IsUtcTimestamp(source.CapturedAtUtc) || source.CapturedAtUtc != snapshot.CapturedAtUtc)
            {
                Add(errors, "invalid_context_source_capture_timestamp", $"{field}.capturedAtUtc", "Every manifest source must use the exact immutable snapshot capture timestamp.");
            }

            if (index < expectedWorkspaceSources.Length)
            {
                var expected = expectedWorkspaceSources[index];
                if (!string.Equals(source.SourceId, expected.Id, StringComparison.Ordinal) || !HasPathSuffix(source.SourcePath, expected.PathSuffix) || source.SourceType != expected.Source || source.Provenance != expected.Provenance || source.TrustClass != expected.Trust || source.Role != expected.Role)
                {
                    Add(errors, "invalid_workspace_context_classification", field, "Workspace sources must preserve the designated AGENTS, role-instruction, and contextual-state order and trust classes.");
                }
            }
            else if (source.SourceType != CustomLoopContextSource.InvokingConversation || source.Provenance != CustomLoopContextProvenance.LogicalConversation || source.TrustClass != CustomLoopContextTrustClass.UntrustedData || source.Role != LlmMessageRole.User)
            {
                Add(errors, "invalid_conversation_context_classification", field, "Sources after the seven workspace entries must be lower-authority logical invoking-conversation data.");
            }

            if (source.SourceType is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.ContextualState && source.UsedCharacterCount > CustomLoopLimits.MaxInstructionCharacters)
            {
                Add(errors, "workspace_context_source_too_large", $"{field}.usedCharacterCount", $"A workspace context source cannot exceed {CustomLoopLimits.MaxInstructionCharacters} admitted characters.");
            }

            if (source.SourceType == CustomLoopContextSource.InvokingConversation && source.Included)
            {
                conversationCharacters += source.UsedCharacterCount;
                includedConversationEntries++;
            }
            else if (source.SourceType == CustomLoopContextSource.InvokingConversation)
            {
                omittedConversationEntries++;
            }
        }

        if (conversationCharacters > CustomLoopLimits.MaxInvokingConversationCharacters)
        {
            Add(errors, "invoking_conversation_manifest_too_large", "contextSnapshot.sourceManifest", $"Included invoking-conversation sources cannot exceed {CustomLoopLimits.MaxInvokingConversationCharacters} characters in aggregate.");
        }

        if (includedConversationEntries > CustomLoopLimits.MaxInvokingConversationEntries)
        {
            Add(errors, "too_many_invoking_conversation_entries", "contextSnapshot.sourceManifest", $"The invoking-conversation snapshot cannot retain more than {CustomLoopLimits.MaxInvokingConversationEntries} selected entries.");
        }

        if (omittedConversationEntries > 1)
        {
            Add(errors, "unaggregated_invoking_conversation_omissions", "contextSnapshot.sourceManifest", "Omitted invoking-conversation history must be represented by at most one aggregate omission entry.");
        }
    }

    private static void ValidateContextManifestCounts(CustomLoopContextManifestSource source, string field, List<CustomLoopValidationError> errors)
    {
        var usedCharacters = source.Content?.Length ?? 0;
        if (source.OriginalCharacterCount < 0 || source.UsedCharacterCount != usedCharacters || source.OriginalCharacterCount < source.UsedCharacterCount)
        {
            Add(errors, "context_source_character_count_mismatch", $"{field}.usedCharacterCount", "Original/used character counts must match the exact retained content without exceeding the original source.");
        }

        if (source.OmissionReason is not null)
        {
            if (source.Content?.Length > 0 || source.UsedCharacterCount != 0 || source.Truncated || source.TruncationReason is not null)
            {
                Add(errors, "invalid_omitted_context_source", field, "An omitted source must retain no content and cannot also be marked truncated.");
            }

            return;
        }

        if (source.Truncated != (source.OriginalCharacterCount > source.UsedCharacterCount) || source.Truncated != (source.TruncationReason is not null))
        {
            Add(errors, "invalid_context_source_truncation", field, "Truncation flag, reason, and original/used character counts must agree.");
        }
    }

    private static bool HasPathSuffix(string? path, string expectedSuffix)
    {
        return path?.Replace('\\', '/').EndsWith(expectedSuffix, StringComparison.Ordinal) == true;
    }

    private static void ValidateEvents(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.Events is null)
        {
            Add(errors, "events_required", "events", "Run event list is required.");
            return;
        }

        if (run.Events.Length == 0)
        {
            Add(errors, "admission_event_required", "events", "A run must retain its admission event.");
            return;
        }

        if (run.Events.Length > CustomLoopLimits.MaxTraceEventsPerRun)
        {
            Add(errors, "too_many_trace_events", "events", $"A run trace cannot retain more than {CustomLoopLimits.MaxTraceEventsPerRun} events.");
        }

        var lifecycleControlEvents = run.Events.Count(item => item is { Kind: CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning });
        if (lifecycleControlEvents > CustomLoopLimits.MaxLifecycleControlEventsPerRun)
        {
            Add(errors, "too_many_lifecycle_control_events", "events", $"A run trace cannot retain more than {CustomLoopLimits.MaxLifecycleControlEventsPerRun} lifecycle/control events.");
        }

        var integrityWarnings = run.Events.Count(item => item is { Kind: CustomLoopRunEventKind.IntegrityWarning });
        if (!run.IsTerminal && lifecycleControlEvents > CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun)
        {
            Add(errors, "terminal_control_slots_not_reserved", "events", $"A nonterminal run can retain at most {CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun} lifecycle/control events so one terminal lifecycle event and one optional post-terminal integrity warning remain possible.");
        }
        else if (run.IsTerminal && integrityWarnings == 0 && lifecycleControlEvents > CustomLoopLimits.MaxTerminalLifecycleControlEventsBeforeIntegrityWarning)
        {
            Add(errors, "integrity_warning_slot_not_reserved", "events", $"A terminal run without its optional integrity warning can retain at most {CustomLoopLimits.MaxTerminalLifecycleControlEventsBeforeIntegrityWarning} lifecycle/control events.");
        }

        if (integrityWarnings > CustomLoopLimits.ReservedPostTerminalIntegrityWarningEventsPerRun)
        {
            Add(errors, "too_many_terminal_integrity_warnings", "events", "A terminal run can retain at most one post-terminal integrity warning.");
        }
        else if (integrityWarnings == 1)
        {
            var warningIndex = Array.FindIndex(run.Events, item => item is { Kind: CustomLoopRunEventKind.IntegrityWarning });
            if (!run.IsTerminal || warningIndex != run.Events.Length - 1 || warningIndex == 0 || run.Events[warningIndex - 1] is not { Kind: CustomLoopRunEventKind.LifecycleChanged })
            {
                Add(errors, "invalid_terminal_integrity_warning_placement", "events", "The one integrity warning must be the final event of a terminal run immediately after its terminal lifecycle event.");
            }
        }

        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var controlExpectedLifecycleVersions = new HashSet<int>();
        DateTimeOffset? previousTimestamp = null;
        for (var index = 0; index < run.Events.Length; index++)
        {
            var item = run.Events[index];
            var field = $"events[{index}]";
            if (item is null)
            {
                Add(errors, "event_required", field, "Run event cannot be null.");
                continue;
            }

            var expectedSequence = index + 1L;
            if (item.Sequence != expectedSequence)
            {
                Add(errors, "non_monotonic_event_sequence", $"{field}.sequence", $"Event sequence must be contiguous and equal to {expectedSequence}.");
            }

            ValidateArtifactId(item.EventId, $"{field}.eventId", errors);
            if (!string.IsNullOrEmpty(item.EventId) && !eventIds.Add(item.EventId))
            {
                Add(errors, "duplicate_event_id", $"{field}.eventId", "Run event ids must be unique.");
            }

            if (!IsUtcTimestamp(item.TimestampUtc) || item.TimestampUtc < run.CreatedAtUtc || item.TimestampUtc > run.UpdatedAtUtc || previousTimestamp is { } previous && item.TimestampUtc < previous)
            {
                Add(errors, "invalid_event_timestamp", $"{field}.timestampUtc", "Event timestamps must be monotonic UTC values within the run timestamp range.");
            }

            previousTimestamp = item.TimestampUtc;
            if (!Enum.IsDefined(item.Kind) || item.Kind == CustomLoopRunEventKind.Unknown)
            {
                Add(errors, "unsupported_event_kind", $"{field}.kind", "Run event kind must be a supported concrete value.");
            }

            if (item.ControlExpectedLifecycleVersion is { } expectedLifecycleVersion)
            {
                if (item.Kind != CustomLoopRunEventKind.LifecycleChanged)
                {
                    Add(errors, "unexpected_control_lifecycle_version", $"{field}.controlExpectedLifecycleVersion", "Only a lifecycle event owned by a control operation may carry its expected lifecycle version.");
                }
                else if (expectedLifecycleVersion < 1 || expectedLifecycleVersion >= run.LifecycleVersion)
                {
                    Add(errors, "invalid_control_lifecycle_version", $"{field}.controlExpectedLifecycleVersion", "A control-owned lifecycle event must identify an earlier positive lifecycle version.");
                }
                else if (!controlExpectedLifecycleVersions.Add(expectedLifecycleVersion))
                {
                    Add(errors, "duplicate_control_lifecycle_version", $"{field}.controlExpectedLifecycleVersion", "A lifecycle source version may be owned by only one durable control transition.");
                }
            }

            ValidateEventCoordinates(item, field, errors);
            var detailLimit = item.Kind is CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning
                ? CustomLoopLimits.MaxLifecycleControlDetailCharacters
                : CustomLoopLimits.MaxRunDetailCharacters;
            ValidateText(item.Detail, $"{field}.detail", detailLimit, required: true, errors);
            ValidateContextBlocks(item.ContextBlocks, $"{field}.contextBlocks", errors);
            ValidateOptionalText(item.CanonicalOutput, $"{field}.canonicalOutput", CustomLoopLimits.MaxCanonicalModelOutputCharacters, errors, requireNormalized: false);
            ValidateOutputMetadata(item, field, errors);
            ValidatePublicationMetadata(item, field, errors);
            ValidateOptionalText(item.Provider, $"{field}.provider", CustomLoopLimits.MaxTraceReferenceCharacters, errors);
            ValidateOptionalText(item.Model, $"{field}.model", CustomLoopLimits.MaxTraceReferenceCharacters, errors);
            ValidateOptionalText(item.ProviderResponseId, $"{field}.providerResponseId", CustomLoopLimits.MaxTraceReferenceCharacters, errors);
            ValidateToolAuthority(item.ToolAuthority, $"{field}.toolAuthority", run, errors);
            ValidateToolEvidence(item.ToolEvidence, $"{field}.toolEvidence", run, errors);
            ValidateTraceReservation(item, field, errors);
            var isToolEvent = item.Kind is CustomLoopRunEventKind.ToolRequestReserved or CustomLoopRunEventKind.ToolGovernanceDecided or CustomLoopRunEventKind.ToolOutcomeObserved or CustomLoopRunEventKind.ToolIntegrityFailed;
            if (isToolEvent && (item.ToolAuthority is null || item.ToolEvidence is null || !ToolAuthoritiesEqual(item.ToolAuthority, item.ToolEvidence.Authority)))
            {
                Add(errors, "tool_evidence_required", field, "Tool trace events require one exact matching authority snapshot and tool-evidence payload.");
            }
            else if (!isToolEvent && item.ToolEvidence is not null)
            {
                Add(errors, "unexpected_tool_evidence", $"{field}.toolEvidence", "Only tool trace events may carry per-request tool evidence.");
            }

            if (item.ToolEvidence is { } toolEvidence && !ToolPhaseMatchesEventKind(toolEvidence.Phase, item.Kind))
            {
                Add(errors, "tool_evidence_phase_mismatch", $"{field}.toolEvidence.phase", "Tool evidence phase must match its durable run-event kind.");
            }

            if (isToolEvent)
            {
                ValidateToolAttemptBinding(run.Events, index, item, field, errors);
            }

            if (item.ToolAuthority is not null && !isToolEvent && item.Kind is not CustomLoopRunEventKind.Admitted and not CustomLoopRunEventKind.NodeAttemptStarted and not CustomLoopRunEventKind.ExitDecisionStarted)
            {
                Add(errors, "unexpected_tool_authority", $"{field}.toolAuthority", "Authority snapshots belong only to admission, attempt-start, or tool trace events.");
            }
            if (item.ExitDecision is { } decision && (!Enum.IsDefined(decision) || decision == CustomLoopExitDecision.Unknown))
            {
                Add(errors, "unsupported_exit_decision", $"{field}.exitDecision", "Exit decision must be a supported concrete value when present.");
            }

            if (item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved && item.CanonicalOutput is null)
            {
                Add(errors, "observed_output_required", $"{field}.canonicalOutput", "Node outcome observation must retain the canonical output.");
            }

            if (item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted && item.ExitDecision is null)
            {
                Add(errors, "exit_decision_required", $"{field}.exitDecision", "Completed Exit-decision events require the parsed decision.");
            }
        }

        if (run.Events[0] is { Sequence: 1, Kind: not CustomLoopRunEventKind.Admitted })
        {
            Add(errors, "first_event_not_admission", "events[0].kind", "The first run event must be the admission event.");
        }

        var admissionAuditMarkers = run.Events.Where(item => item is { Kind: CustomLoopRunEventKind.AdmissionAuditCompleted }).ToArray();
        if (admissionAuditMarkers.Length > 1)
        {
            Add(errors, "duplicate_admission_audit_marker", "events", "A run may retain exactly one admission-audit completion marker.");
        }

        if (admissionAuditMarkers.Length == 1)
        {
            var marker = admissionAuditMarkers[0];
            var markerIndex = Array.IndexOf(run.Events, marker);
            if (markerIndex != 1 || marker.Sequence != 2)
            {
                Add(errors, "misordered_admission_audit_marker", $"events[{markerIndex}].kind", "The admission-audit completion marker must be the second durable run event.");
            }

            if (marker.Iteration is not null || marker.StepId is not null || marker.Attempt is not null || marker.ContextBlocks is not { Length: 0 }
                || marker.CanonicalOutput is not null || marker.OriginalOutputCharacterCount is not null || marker.CanonicalOutputTruncated is not null
                || marker.RetainedForLoopReasoning is not null || marker.PublishedToInvokingConversation is not null || marker.ConversationPublicationId is not null
                || marker.Provider is not null || marker.Model is not null || marker.ProviderResponseId is not null || marker.ExitDecision is not null || marker.ToolAuthority is not null || marker.ToolEvidence is not null || marker.TraceReservationUtf8Bytes is not null || marker.ControlExpectedLifecycleVersion is not null)
            {
                Add(errors, "invalid_admission_audit_marker", $"events[{markerIndex}]", "The admission-audit completion marker cannot carry prompt, output, provider, publication, or node-attempt data.");
            }
        }
    }

    private static void ValidateTraceReservation(CustomLoopRunEvent item, string field, List<CustomLoopValidationError> errors)
    {
        var startsAttempt = item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted;
        if (startsAttempt && item.TraceReservationUtf8Bytes != CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes)
        {
            Add(errors, "attempt_trace_reservation_required", $"{field}.traceReservationUtf8Bytes", "Every provider-attempt start must atomically reserve the bounded mandatory outcome footprint before dispatch.");
        }
        else if (!startsAttempt && item.TraceReservationUtf8Bytes is not null)
        {
            Add(errors, "unexpected_trace_reservation", $"{field}.traceReservationUtf8Bytes", "Only provider-attempt start events may carry an attempt trace reservation.");
        }
    }

    private static bool ToolPhaseMatchesEventKind(CustomLoopToolEvidencePhase phase, CustomLoopRunEventKind kind)
    {
        return phase switch
        {
            CustomLoopToolEvidencePhase.RequestReserved => kind == CustomLoopRunEventKind.ToolRequestReserved,
            CustomLoopToolEvidencePhase.GovernanceDecided => kind == CustomLoopRunEventKind.ToolGovernanceDecided,
            CustomLoopToolEvidencePhase.OutcomeObserved => kind == CustomLoopRunEventKind.ToolOutcomeObserved,
            CustomLoopToolEvidencePhase.IntegrityFailed => kind == CustomLoopRunEventKind.ToolIntegrityFailed,
            _ => false
        };
    }

    private static void ValidateOutputMetadata(CustomLoopRunEvent item, string field, List<CustomLoopValidationError> errors)
    {
        if (item.CanonicalOutput is null)
        {
            if (item.OriginalOutputCharacterCount is not null || item.CanonicalOutputTruncated is not null)
            {
                Add(errors, "unexpected_output_metadata", $"{field}.originalOutputCharacterCount", "Output length and truncation metadata require a canonical output.");
            }

            return;
        }

        if (item.OriginalOutputCharacterCount is not { } originalCount || item.CanonicalOutputTruncated is not { } truncated)
        {
            Add(errors, "output_metadata_required", $"{field}.originalOutputCharacterCount", "Canonical output requires original character count and truncation metadata.");
            return;
        }

        if (originalCount < item.CanonicalOutput.Length || truncated != (originalCount > item.CanonicalOutput.Length))
        {
            Add(errors, "inconsistent_output_metadata", $"{field}.originalOutputCharacterCount", "Original output length and truncation flag must match the canonical retained output.");
        }
    }

    private static void ValidatePublicationMetadata(CustomLoopRunEvent item, string field, List<CustomLoopValidationError> errors)
    {
        var isPublicationProtocolEvent = item.Kind is CustomLoopRunEventKind.ConversationPublicationStarted or CustomLoopRunEventKind.ConversationPublished;
        if (item.PublishedToInvokingConversation == true || isPublicationProtocolEvent)
        {
            if (!CustomLoopArtifactIdentifier.IsValid(item.ConversationPublicationId))
            {
                Add(errors, "conversation_publication_id_required", $"{field}.conversationPublicationId", "Published conversation output requires a safe idempotency correlation id.");
            }
        }
        else if (item.ConversationPublicationId is not null)
        {
            Add(errors, "unexpected_conversation_publication_id", $"{field}.conversationPublicationId", "Conversation publication id is present without a published outcome.");
        }

        if (item.Kind == CustomLoopRunEventKind.ConversationPublished && item.PublishedToInvokingConversation is null)
        {
            Add(errors, "conversation_publication_outcome_required", $"{field}.publishedToInvokingConversation", "ConversationPublished must record a definite success or failure outcome.");
        }
    }

    private static void ValidateEventCoordinates(CustomLoopRunEvent item, string field, List<CustomLoopValidationError> errors)
    {
        if (item.Iteration is < 1)
        {
            Add(errors, "invalid_event_iteration", $"{field}.iteration", "Event iteration must be at least 1 when present.");
        }

        if (item.Attempt is < 1)
        {
            Add(errors, "invalid_event_attempt", $"{field}.attempt", "Event attempt must be at least 1 when present.");
        }

        if (item.StepId is not null)
        {
            ValidateArtifactId(item.StepId, $"{field}.stepId", errors);
        }

        var isNodeEvent = item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.NodeAttemptFailed or CustomLoopRunEventKind.ToolRequestReserved or CustomLoopRunEventKind.ToolGovernanceDecided or CustomLoopRunEventKind.ToolOutcomeObserved or CustomLoopRunEventKind.ToolIntegrityFailed;
        if (isNodeEvent && (item.Iteration is null || item.StepId is null || item.Attempt is null))
        {
            Add(errors, "node_event_coordinates_required", field, "Node attempt events require iteration, step id, and attempt.");
        }

        var isExitEvent = item.Kind is CustomLoopRunEventKind.ExitDecisionStarted or CustomLoopRunEventKind.ExitDecisionCompleted;
        if (isExitEvent && (item.Iteration is null || item.Attempt is null))
        {
            Add(errors, "exit_event_coordinates_required", field, "Exit decision events require iteration and attempt.");
        }

        if (item.Kind is CustomLoopRunEventKind.IterationStarted or CustomLoopRunEventKind.CheckpointCommitted or CustomLoopRunEventKind.ConversationPublicationStarted or CustomLoopRunEventKind.ConversationPublished && item.Iteration is null)
        {
            Add(errors, "iteration_coordinate_required", $"{field}.iteration", "This run event requires an iteration coordinate.");
        }
    }

    private static void ValidateContextBlocks(CustomLoopContextBlock[]? blocks, string field, List<CustomLoopValidationError> errors)
    {
        if (blocks is null)
        {
            Add(errors, "context_blocks_required", field, "Context block list is required, even when empty.");
            return;
        }

        for (var index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            var blockField = $"{field}[{index}]";
            if (block is null)
            {
                Add(errors, "context_block_required", blockField, "Context block cannot be null.");
                continue;
            }

            if (!Enum.IsDefined(block.Source) || block.Source == CustomLoopContextSource.Unknown)
            {
                Add(errors, "unsupported_context_source", $"{blockField}.source", "Context source must be a supported concrete value.");
            }

            if (!Enum.IsDefined(block.Role) || block.Role == LlmMessageRole.Unknown)
            {
                Add(errors, "unsupported_context_role", $"{blockField}.role", "Context role must be a supported concrete value.");
            }

            ValidateText(block.SourceId, $"{blockField}.sourceId", CustomLoopLimits.MaxTraceReferenceCharacters, required: true, errors);
            ValidateOptionalText(block.SourceVersion, $"{blockField}.sourceVersion", CustomLoopLimits.MaxTraceReferenceCharacters, errors);
            ValidateText(block.Content, $"{blockField}.content", CustomLoopLimits.MaxLogicalProviderRequestCharacters, required: block.Included, errors, requireNormalized: false);
            ValidateOptionalText(block.OmissionReason, $"{blockField}.omissionReason", CustomLoopLimits.MaxRunDetailCharacters, errors);
            if (block.Included && block.OmissionReason is not null)
            {
                Add(errors, "unexpected_omission_reason", $"{blockField}.omissionReason", "Included context cannot also have an omission reason.");
            }

            if (!block.Included && string.IsNullOrWhiteSpace(block.OmissionReason))
            {
                Add(errors, "omission_reason_required", $"{blockField}.omissionReason", "Omitted context requires an explicit reason.");
            }

            var retainedCharacterCount = block.Content?.Length ?? 0;
            if (block.CharacterCount < retainedCharacterCount || block.Truncated != (block.CharacterCount > retainedCharacterCount))
            {
                Add(errors, "context_character_count_mismatch", $"{blockField}.characterCount", "Context source length and truncation flag must match the canonical retained content.");
            }

            ValidateContentHash(block.Content, block.ContentHash, $"{blockField}.contentHash", errors);
            if (block.Source == CustomLoopContextSource.HarnessGovernance)
            {
                if (!string.Equals(block.SourceVersion, EmbodySenseDeveloperInstructions.CurrentVersion, StringComparison.Ordinal) || block.Role != LlmMessageRole.System || !block.Included)
                {
                    Add(errors, "invalid_governance_context_block", blockField, "Harness governance evidence must be included in the system instruction channel with the exact current governance version.");
                }
            }
            else if (block.SourceVersion is not null)
            {
                Add(errors, "unexpected_context_source_version", $"{blockField}.sourceVersion", "Only versioned fixed harness governance may carry a context source version.");
            }
        }
    }

    private static void ValidateToolAuthority(CustomLoopToolAuthoritySnapshot? authority, string field, CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (authority is null)
        {
            return;
        }

        ValidateArtifactId(authority.RoleId, $"{field}.roleId", errors);
        ValidateAssignmentSet(authority.AdmittedMaximum, $"{field}.admittedMaximum", errors);
        ValidateAssignmentSet(authority.CurrentRoleCeiling, $"{field}.currentRoleCeiling", errors);
        ValidateAssignmentSet(authority.ImplementedCatalog, $"{field}.implementedCatalog", errors);
        ValidateAssignmentSet(authority.EffectiveAssignments, $"{field}.effectiveAssignments", errors);
        ValidateHash(authority.RoleCeilingHash, $"{field}.roleCeilingHash", errors);
        ValidateHash(authority.CatalogHash, $"{field}.catalogHash", errors);
        ValidateText(authority.Detail, $"{field}.detail", CustomLoopLimits.MaxToolGovernanceDetailCharacters, required: true, errors);
        if (!IsUtcTimestamp(authority.EvaluatedAtUtc) || authority.EvaluatedAtUtc > run.UpdatedAtUtc)
        {
            Add(errors, "invalid_authority_timestamp", $"{field}.evaluatedAtUtc", "Authority evaluation timestamp must be UTC and no later than the containing trace update.");
        }

        if (!authority.EffectiveAssignments.All(authority.AdmittedMaximum.Contains) || !authority.EffectiveAssignments.All(authority.CurrentRoleCeiling.Contains) || !authority.EffectiveAssignments.All(authority.ImplementedCatalog.Contains))
        {
            Add(errors, "authority_intersection_widened", $"{field}.effectiveAssignments", "Effective assignments must be an intersection of the admitted maximum, current role ceiling, and implemented catalog.");
        }
    }

    private static void ValidateToolEvidence(CustomLoopToolTraceEvidence? evidence, string field, CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (evidence is null)
        {
            return;
        }

        if (!Enum.IsDefined(evidence.Phase) || evidence.Phase == CustomLoopToolEvidencePhase.Unknown)
        {
            Add(errors, "unsupported_tool_evidence_phase", $"{field}.phase", "Tool evidence phase must be concrete.");
        }

        if (evidence.RequestOrdinal < 1 || evidence.RequestOrdinal > CustomLoopLimits.MaxGovernedToolRequestsPerAttempt + 1)
        {
            Add(errors, "tool_request_ordinal_out_of_range", $"{field}.requestOrdinal", "Tool request ordinal is outside the per-attempt limit.");
        }

        ValidateArtifactId(evidence.RequestCorrelationId, $"{field}.requestCorrelationId", errors);
        if (evidence.BrokerRequestId is not null)
        {
            ValidateArtifactId(evidence.BrokerRequestId, $"{field}.brokerRequestId", errors);
        }

        if (!Enum.IsDefined(evidence.Command))
        {
            Add(errors, "unsupported_tool_command", $"{field}.command", "Tool command must be a supported concrete value.");
        }

        ValidateText(evidence.TargetPath, $"{field}.targetPath", CustomLoopLimits.MaxGovernedToolTargetCharacters, required: true, errors);
        ValidateOptionalText(evidence.Content, $"{field}.content", CustomLoopLimits.MaxGovernedToolArgumentCharacters, errors, requireNormalized: false);
        ValidateOptionalText(evidence.Pattern, $"{field}.pattern", CustomLoopLimits.MaxGovernedToolArgumentCharacters, errors, requireNormalized: false);
        ValidateOptionalText(evidence.ResolvedTarget, $"{field}.resolvedTarget", CustomLoopLimits.MaxGovernedToolTargetCharacters, errors, requireNormalized: false);
        if (evidence.ReservedUtf8Bytes != CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes)
        {
            Add(errors, "invalid_tool_evidence_reservation", $"{field}.reservedUtf8Bytes", "Every governed request must reserve the server-owned worst-case evidence allowance before dispatch.");
        }

        if (evidence.Governance is { } governance)
        {
            if (!Enum.IsDefined(governance.AuthorityDecision) || governance.AuthorityDecision == ToolAuthorityDecision.Unknown || !Enum.IsDefined(governance.ApprovalDecision) || governance.ApprovalDecision == ToolApprovalDecision.Unknown)
            {
                Add(errors, "invalid_tool_governance_decision", $"{field}.governance", "Tool governance decisions must use concrete values.");
            }

            if (governance.PermissionDecision is { } permissionDecision && !Enum.IsDefined(permissionDecision))
            {
                Add(errors, "invalid_permission_decision", $"{field}.governance.permissionDecision", "Permission decision must be supported when present.");
            }

            ValidateText(governance.AuthorityDetail, $"{field}.governance.authorityDetail", CustomLoopLimits.MaxToolGovernanceDetailCharacters, required: true, errors);
            ValidateOptionalText(governance.PermissionMatchedPath, $"{field}.governance.permissionMatchedPath", CustomLoopLimits.MaxGovernedToolTargetCharacters, errors, requireNormalized: false);
            ValidateOptionalText(governance.PermissionDetail, $"{field}.governance.permissionDetail", CustomLoopLimits.MaxToolGovernanceDetailCharacters, errors);
            ValidateOptionalText(governance.PermissionPolicyHash, $"{field}.governance.permissionPolicyHash", CustomLoopLimits.Sha256HexCharacters, errors);
            ValidateOptionalText(governance.ApprovalDecisionBy, $"{field}.governance.approvalDecisionBy", CustomLoopLimits.MaxToolGovernanceDetailCharacters, errors);
            ValidateOptionalText(governance.ApprovalDetail, $"{field}.governance.approvalDetail", CustomLoopLimits.MaxToolGovernanceDetailCharacters, errors);
            if (governance.PermissionPolicyHash is not null)
            {
                ValidateHash(governance.PermissionPolicyHash, $"{field}.governance.permissionPolicyHash", errors);
            }
        }

        if (evidence.Phase == CustomLoopToolEvidencePhase.RequestReserved && (evidence.BrokerRequestId is not null || evidence.Governance is not null || evidence.Outcome is not null || evidence.CanonicalResultReturnedToModel is not null || evidence.ReturnedToModel))
        {
            Add(errors, "invalid_tool_reservation_payload", field, "Request reservation may contain only the exact request, authority snapshot, correlation, and reserved capacity.");
        }

        if (evidence.Phase == CustomLoopToolEvidencePhase.GovernanceDecided && (evidence.BrokerRequestId is null || evidence.Governance is null || evidence.Outcome is not null || evidence.CanonicalResultReturnedToModel is not null || evidence.ReturnedToModel))
        {
            Add(errors, "invalid_tool_governance_payload", field, "Governance evidence requires broker correlation and decisions but no result.");
        }

        if (evidence.Phase == CustomLoopToolEvidencePhase.OutcomeObserved)
        {
            if (evidence.BrokerRequestId is null || evidence.Governance is null || evidence.Outcome is null || evidence.CanonicalResultReturnedToModel is null || evidence.CanonicalResultHash is null || evidence.CanonicalResultCharacterCount != evidence.CanonicalResultReturnedToModel?.Length)
            {
                Add(errors, "incomplete_tool_outcome", field, "Tool outcome evidence requires exact broker, governance, outcome, and canonical model-result data.");
            }
            else
            {
                ValidateText(evidence.CanonicalResultReturnedToModel, $"{field}.canonicalResultReturnedToModel", CustomLoopLimits.MaxCanonicalToolResultCharacters, required: true, errors, requireNormalized: false);
                ValidateContentHash(evidence.CanonicalResultReturnedToModel, evidence.CanonicalResultHash, $"{field}.canonicalResultHash", errors);
            }
        }
    }

    private static void ValidateAssignmentSet(CustomLoopToolAssignment[]? assignments, string field, List<CustomLoopValidationError> errors)
    {
        if (assignments is null)
        {
            Add(errors, "tool_assignment_set_required", field, "Authority assignment sets are required even when empty.");
            return;
        }

        if (assignments.Any(value => !Enum.IsDefined(value) || value == CustomLoopToolAssignment.Unknown) || assignments.Distinct().Count() != assignments.Length)
        {
            Add(errors, "invalid_tool_assignment_set", field, "Authority assignment sets must contain unique implemented list, read, or search values.");
        }
    }

    private static void ValidateCheckpoint(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        var checkpoint = run.Checkpoint;
        if (checkpoint is null)
        {
            Add(errors, "checkpoint_required", "checkpoint", "A restart-safe checkpoint is required.");
            return;
        }

        var maximumIterations = (run.AdmittedDefinition?.ExitPolicy?.MaxAdditionalIterations ?? 0) + 1;
        var stepCount = run.AdmittedDefinition?.InferenceSteps?.Length ?? 0;
        if (checkpoint.Iteration < 1 || checkpoint.Iteration > maximumIterations)
        {
            Add(errors, "checkpoint_iteration_out_of_range", "checkpoint.iteration", $"Checkpoint iteration must be between 1 and {maximumIterations}.");
        }

        if (checkpoint.AcceptedRepeatCount < 0 || checkpoint.AcceptedRepeatCount >= maximumIterations || checkpoint.Iteration != checkpoint.AcceptedRepeatCount + 1)
        {
            Add(errors, "checkpoint_repeat_count_mismatch", "checkpoint.acceptedRepeatCount", "Accepted repeat count must be nonnegative and exactly one less than the current iteration.");
        }

        if (checkpoint.NextStepIndex < 0 || checkpoint.NextStepIndex > stepCount)
        {
            Add(errors, "checkpoint_step_out_of_range", "checkpoint.nextStepIndex", $"Next step index must be between 0 and {stepCount}.");
        }

        if (checkpoint.PendingExitDecision && (checkpoint.NextStepIndex != stepCount || checkpoint.AcceptedRepeatCount >= maximumIterations - 1))
        {
            Add(errors, "invalid_pending_exit_checkpoint", "checkpoint.pendingExitDecision", "Pending Exit decision requires all steps complete and remaining repeat authority.");
        }

        if (checkpoint.ToolRequestsUsed < 0 || checkpoint.ToolRequestsUsed > CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun)
        {
            Add(errors, "tool_request_budget_out_of_range", "checkpoint.toolRequestsUsed", $"Persisted tool-request usage must be between 0 and {CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun}, including the one visible over-limit denial.");
        }

        if (checkpoint.EarlierRetainedOutputs is null)
        {
            Add(errors, "retained_outputs_required", "checkpoint.earlierRetainedOutputs", "Earlier retained output list is required, even when empty.");
        }
        else
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < checkpoint.EarlierRetainedOutputs.Length; index++)
            {
                var output = checkpoint.EarlierRetainedOutputs[index];
                ValidateRetainedOutput(output, $"checkpoint.earlierRetainedOutputs[{index}]", run, errors);
                if (output is not null && !identities.Add($"{output.Iteration}:{output.StepId}"))
                {
                    Add(errors, "duplicate_retained_output", $"checkpoint.earlierRetainedOutputs[{index}]", "Earlier retained output identity must be unique per iteration and step.");
                }
            }
        }

        ValidateRetainedOutput(checkpoint.PreviousIterationResult, "checkpoint.previousIterationResult", run, errors, optional: true);
        if (checkpoint.PreviousIterationResult is { } previous && previous.Iteration != checkpoint.Iteration - 1)
        {
            Add(errors, "invalid_previous_iteration_result", "checkpoint.previousIterationResult.iteration", "Previous iteration result must belong to the immediately preceding iteration.");
        }

        ValidateRetainedOutput(checkpoint.CurrentIterationResult, "checkpoint.currentIterationResult", run, errors, optional: true);
        if (checkpoint.CurrentIterationResult is { } current && current.Iteration != checkpoint.Iteration)
        {
            Add(errors, "invalid_current_iteration_result", "checkpoint.currentIterationResult.iteration", "Current iteration result must belong to the checkpoint iteration.");
        }

        var lastEventSequence = run.Events?.LastOrDefault()?.Sequence ?? 0;
        if (checkpoint.LastCommittedSequence < 0 || checkpoint.LastCommittedSequence > lastEventSequence)
        {
            Add(errors, "checkpoint_sequence_out_of_range", "checkpoint.lastCommittedSequence", "Last committed sequence must identify a retained event or zero before the first checkpoint.");
        }
        else if (checkpoint.LastCommittedSequence > 0 && run.Events![(int)checkpoint.LastCommittedSequence - 1].Kind != CustomLoopRunEventKind.CheckpointCommitted)
        {
            Add(errors, "checkpoint_sequence_not_commit", "checkpoint.lastCommittedSequence", "Last committed sequence must identify a CheckpointCommitted event.");
        }
    }

    private static void ValidateRetainedOutput(CustomLoopRetainedOutput? output, string field, CustomLoopRunRecord run, List<CustomLoopValidationError> errors, bool optional = false)
    {
        if (output is null)
        {
            if (!optional)
            {
                Add(errors, "retained_output_required", field, "Retained output cannot be null.");
            }

            return;
        }

        ValidateArtifactId(output.StepId, $"{field}.stepId", errors);
        if (run.AdmittedDefinition?.InferenceSteps is { } steps && !steps.Any(step => string.Equals(step.Id, output.StepId, StringComparison.Ordinal)))
        {
            Add(errors, "unknown_retained_step", $"{field}.stepId", "Retained output step id must exist in the admitted definition.");
        }

        if (output.Iteration < 1 || output.Iteration > run.Checkpoint.Iteration)
        {
            Add(errors, "retained_output_iteration_out_of_range", $"{field}.iteration", "Retained output iteration must be within the executed checkpoint range.");
        }

        ValidateText(output.Content, $"{field}.content", CustomLoopLimits.MaxCanonicalModelOutputCharacters, required: false, errors, requireNormalized: false);
        ValidateContentHash(output.Content, output.ContentHash, $"{field}.contentHash", errors);
    }

    private static void ValidateOutcome(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        ValidateOptionalText(run.FinalOutput, "finalOutput", CustomLoopLimits.MaxCanonicalModelOutputCharacters, errors, requireNormalized: false);
        ValidateOptionalText(run.FailureCode, "failureCode", CustomLoopLimits.MaxTraceReferenceCharacters, errors);
        ValidateOptionalText(run.FailureDetail, "failureDetail", CustomLoopLimits.MaxRunDetailCharacters, errors);

        if (run.Status == CustomLoopRunStatus.Completed)
        {
            if (run.FinalOutput is null)
            {
                Add(errors, "final_output_required", "finalOutput", "Completed runs require a canonical final output.");
            }

            if (run.FailureCode is not null || run.FailureDetail is not null)
            {
                Add(errors, "unexpected_failure", "failureCode", "Completed runs cannot retain a failure outcome.");
            }
        }
        else if (run.FinalOutput is not null)
        {
            Add(errors, "unexpected_final_output", "finalOutput", "Only completed runs may have a final output.");
        }

        if (run.Status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview && (string.IsNullOrWhiteSpace(run.FailureCode) || string.IsNullOrWhiteSpace(run.FailureDetail)))
        {
            Add(errors, "failure_detail_required", "failureCode", "Failed and needs-review runs require a safe failure code and detail.");
        }

        if (!run.IsTerminal && (run.FailureCode is not null || run.FailureDetail is not null))
        {
            Add(errors, "unexpected_nonterminal_failure", "failureCode", "Nonterminal runs cannot have a terminal failure outcome.");
        }

        if ((run.FailureCode is null) != (run.FailureDetail is null))
        {
            Add(errors, "incomplete_failure_outcome", "failureDetail", "Failure code and detail must be present together.");
        }
    }

    private static void ValidateImmutableAdmission(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.SchemaVersion != candidate.SchemaVersion || !string.Equals(current.Id, candidate.Id, StringComparison.Ordinal) || !string.Equals(current.LoopId, candidate.LoopId, StringComparison.Ordinal) || current.CreatedAtUtc != candidate.CreatedAtUtc || !string.Equals(current.Surface, candidate.Surface, StringComparison.Ordinal) || !Equals(current.ModelSnapshot, candidate.ModelSnapshot) || !string.Equals(current.AdmissionOperationId, candidate.AdmissionOperationId, StringComparison.Ordinal) || !string.Equals(current.AdmissionActor, candidate.AdmissionActor, StringComparison.Ordinal) || !string.Equals(current.AdmissionRequestHash, candidate.AdmissionRequestHash, StringComparison.Ordinal) || !string.Equals(current.TriggerPrompt, candidate.TriggerPrompt, StringComparison.Ordinal))
        {
            Add(errors, "admission_identity_changed", "$", "Run identity and admission-owned scalar fields are immutable.");
        }

        if (current.AdmittedDefinition is null || candidate.AdmittedDefinition is null || !string.Equals(current.AdmittedDefinition.Id, candidate.AdmittedDefinition.Id, StringComparison.Ordinal) || current.AdmittedDefinition.DefinitionVersion != candidate.AdmittedDefinition.DefinitionVersion || !string.Equals(current.AdmittedDefinition.ContentHash, candidate.AdmittedDefinition.ContentHash, StringComparison.Ordinal))
        {
            Add(errors, "admitted_definition_changed", "admittedDefinition", "The canonical admitted definition identity and content are immutable.");
        }

        if (!Equals(current.InvokingConversation, candidate.InvokingConversation) || !ContextSnapshotsEqual(current.ContextSnapshot, candidate.ContextSnapshot))
        {
            Add(errors, "admitted_context_changed", "contextSnapshot", "The admitted conversation binding and context snapshot are immutable.");
        }
    }

    private static void ValidateLifecycleTransition(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (!IsAllowedLifecycleTransition(current.Status, candidate.Status))
        {
            Add(errors, "invalid_lifecycle_transition", "status", $"Lifecycle transition from {current.Status} to {candidate.Status} is not allowed.");
        }

        if (current.Status != candidate.Status && (candidate.Events?.Skip(current.Events?.Length ?? 0).Any(item => item is { Kind: CustomLoopRunEventKind.LifecycleChanged }) != true))
        {
            Add(errors, "lifecycle_event_required", "events", "A lifecycle transition must append a LifecycleChanged event.");
        }
    }

    private static void ValidateAppendOnlyEvents(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.Events is null || candidate.Events is null || candidate.Events.Length < current.Events.Length)
        {
            Add(errors, "event_history_truncated", "events", "Persisted run events are append-only.");
            return;
        }

        for (var index = 0; index < current.Events.Length; index++)
        {
            if (!EventsEqual(current.Events[index], candidate.Events[index]))
            {
                Add(errors, "event_history_changed", $"events[{index}]", "Previously persisted run events are immutable.");
            }
        }
    }

    private static void ValidateToolAttemptBinding(CustomLoopRunEvent[] events, int eventIndex, CustomLoopRunEvent item, string field, List<CustomLoopValidationError> errors)
    {
        var attemptStart = events.Take(eventIndex).LastOrDefault(candidate => candidate is not null
            && (candidate.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted)
            && candidate.Iteration == item.Iteration
            && string.Equals(candidate.StepId, item.StepId, StringComparison.Ordinal)
            && candidate.Attempt == item.Attempt);
        if (attemptStart?.ToolAuthority is null)
        {
            Add(errors, "tool_attempt_start_required", field, "Tool trace evidence must follow a matching provider-attempt start with an exact authority snapshot.");
            return;
        }

        if (item.ToolAuthority is null || !item.ToolAuthority.IsBoundedRefreshOf(attemptStart.ToolAuthority)
            || item.ToolEvidence is { } evidence && !evidence.Authority.IsBoundedRefreshOf(attemptStart.ToolAuthority))
        {
            Add(errors, "tool_authority_not_attempt_bound", $"{field}.toolAuthority", "Every tool trace phase must retain a fresh non-widening authority snapshot bound to the matching attempt-start admission maximum and catalog.");
        }

        if (item.ToolEvidence is { } toolEvidence && !attemptStart.ToolAuthority.AllowsCommand(toolEvidence.Command))
        {
            Add(errors, "tool_command_not_attempt_authorized", $"{field}.toolEvidence.command", "The governed tool command must be included in the matching attempt-start effective authority.");
        }
    }

    private static void ValidateAppendedControlOwnership(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.Events is null || candidate.Events is null)
        {
            return;
        }

        foreach (var item in candidate.Events.Skip(current.Events.Length))
        {
            if (item?.ControlExpectedLifecycleVersion is { } expectedLifecycleVersion && expectedLifecycleVersion != current.LifecycleVersion)
            {
                var index = Array.IndexOf(candidate.Events, item);
                Add(errors, "control_lifecycle_version_mismatch", $"events[{index}].controlExpectedLifecycleVersion", "A newly appended control-owned lifecycle event must identify the exact persisted lifecycle version used for compare-and-swap.");
            }
        }
    }

    private static void ValidateMonotonicCheckpoint(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.Checkpoint is null || candidate.Checkpoint is null)
        {
            return;
        }

        if (candidate.Checkpoint.Iteration < current.Checkpoint.Iteration || candidate.Checkpoint.Iteration > current.Checkpoint.Iteration + 1 || candidate.Checkpoint.AcceptedRepeatCount < current.Checkpoint.AcceptedRepeatCount || candidate.Checkpoint.ToolRequestsUsed < current.Checkpoint.ToolRequestsUsed || candidate.Checkpoint.LastCommittedSequence < current.Checkpoint.LastCommittedSequence)
        {
            Add(errors, "checkpoint_regressed", "checkpoint", "Checkpoint iteration, repeat count, tool-call usage, and committed sequence must advance monotonically without skipping an iteration.");
        }

        if (candidate.Checkpoint.Iteration == current.Checkpoint.Iteration && candidate.Checkpoint.NextStepIndex < current.Checkpoint.NextStepIndex)
        {
            Add(errors, "checkpoint_step_regressed", "checkpoint.nextStepIndex", "Next step cannot move backward within an iteration.");
        }

        if (candidate.Checkpoint.Iteration > current.Checkpoint.Iteration && candidate.Checkpoint.NextStepIndex != 0)
        {
            Add(errors, "repeated_iteration_not_at_start", "checkpoint.nextStepIndex", "A newly accepted repeat iteration must restart at the first inference step.");
        }

        if (current.Checkpoint.EarlierRetainedOutputs is null || candidate.Checkpoint.EarlierRetainedOutputs is null)
        {
            Add(errors, "retained_output_history_truncated", "checkpoint.earlierRetainedOutputs", "Earlier retained output lists must be present.");
            return;
        }

        if (candidate.Checkpoint.Iteration > current.Checkpoint.Iteration)
        {
            if (candidate.Checkpoint.EarlierRetainedOutputs.Length != 0)
            {
                Add(errors, "repeated_iteration_retained_outputs_not_reset", "checkpoint.earlierRetainedOutputs", "A repeated iteration must reset its same-iteration retained-output list.");
            }

            return;
        }

        if (candidate.Checkpoint.EarlierRetainedOutputs.Length < current.Checkpoint.EarlierRetainedOutputs.Length)
        {
            Add(errors, "retained_output_history_truncated", "checkpoint.earlierRetainedOutputs", "Earlier retained outputs are append-only within an iteration.");
            return;
        }

        for (var index = 0; index < current.Checkpoint.EarlierRetainedOutputs.Length; index++)
        {
            if (!Equals(current.Checkpoint.EarlierRetainedOutputs[index], candidate.Checkpoint.EarlierRetainedOutputs[index]))
            {
                Add(errors, "retained_output_history_changed", $"checkpoint.earlierRetainedOutputs[{index}]", "Previously retained outputs are immutable.");
            }
        }
    }

    private static void ValidateMonotonicExecutionClock(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.ExecutionClock is not null && candidate.ExecutionClock is not null && candidate.ExecutionClock.AccumulatedRunningMilliseconds < current.ExecutionClock.AccumulatedRunningMilliseconds)
        {
            Add(errors, "execution_clock_regressed", "executionClock.accumulatedRunningMilliseconds", "Accumulated running time cannot move backward.");
        }
    }

    private static bool EventsEqual(CustomLoopRunEvent? left, CustomLoopRunEvent? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.Sequence == right.Sequence
            && string.Equals(left.EventId, right.EventId, StringComparison.Ordinal)
            && left.TimestampUtc == right.TimestampUtc
            && left.Kind == right.Kind
            && left.Iteration == right.Iteration
            && string.Equals(left.StepId, right.StepId, StringComparison.Ordinal)
            && left.Attempt == right.Attempt
            && string.Equals(left.Detail, right.Detail, StringComparison.Ordinal)
            && left.ContextBlocks is not null
            && right.ContextBlocks is not null
            && left.ContextBlocks.SequenceEqual(right.ContextBlocks)
            && string.Equals(left.CanonicalOutput, right.CanonicalOutput, StringComparison.Ordinal)
            && left.OriginalOutputCharacterCount == right.OriginalOutputCharacterCount
            && left.CanonicalOutputTruncated == right.CanonicalOutputTruncated
            && left.RetainedForLoopReasoning == right.RetainedForLoopReasoning
            && left.PublishedToInvokingConversation == right.PublishedToInvokingConversation
            && string.Equals(left.ConversationPublicationId, right.ConversationPublicationId, StringComparison.Ordinal)
            && string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
            && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
            && string.Equals(left.ProviderResponseId, right.ProviderResponseId, StringComparison.Ordinal)
            && left.ExitDecision == right.ExitDecision
            && ToolAuthoritiesEqual(left.ToolAuthority, right.ToolAuthority)
            && ToolEvidenceEqual(left.ToolEvidence, right.ToolEvidence)
            && left.TraceReservationUtf8Bytes == right.TraceReservationUtf8Bytes
            && left.ControlExpectedLifecycleVersion == right.ControlExpectedLifecycleVersion;
    }

    private static bool ToolAuthoritiesEqual(CustomLoopToolAuthoritySnapshot? left, CustomLoopToolAuthoritySnapshot? right)
    {
        return ReferenceEquals(left, right) || left?.Matches(right) == true;
    }

    private static bool ToolEvidenceEqual(CustomLoopToolTraceEvidence? left, CustomLoopToolTraceEvidence? right)
    {
        return ReferenceEquals(left, right)
            || left is not null && right is not null
            && left with { Authority = right.Authority } == right
            && ToolAuthoritiesEqual(left.Authority, right.Authority);
    }

    private static bool ContextSnapshotsEqual(CustomLoopContextSnapshot? left, CustomLoopContextSnapshot? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && left.SchemaVersion == right.SchemaVersion
            && left.CapturedAtUtc == right.CapturedAtUtc
            && string.Equals(left.ManifestHash, right.ManifestHash, StringComparison.Ordinal)
            && left.SourceManifest is not null
            && right.SourceManifest is not null
            && left.SourceManifest.SequenceEqual(right.SourceManifest);
    }

}
