using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using static EmbodySense.Core.Common.Loops.Custom.Execution.CustomLoopRunValidationRules;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Validates custom loop runs.
/// </summary>
public static class CustomLoopRunValidator
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private static readonly string[] _sequentialToolFreeCapabilityRootIds =
    [
        "org.embodysense/conversation-turn",
        "org.embodysense/model-inference",
    ];
    private static readonly string[] _sequentialToolEnabledCapabilityRootIds =
    [
        "org.embodysense/conversation-turn",
        "org.embodysense/model-inference",
        "org.embodysense/workspace-command",
    ];
    private static readonly string[] _sequentialScheduledToolFreeCapabilityRootIds =
    [
        "org.embodysense/conversation-turn",
        "org.embodysense/model-inference",
        "org.embodysense/triggers/time",
    ];
    private static readonly string[] _sequentialScheduledToolEnabledCapabilityRootIds =
    [
        "org.embodysense/conversation-turn",
        "org.embodysense/model-inference",
        "org.embodysense/triggers/time",
        "org.embodysense/workspace-command",
    ];
    private static readonly CustomLoopToolAssignment[] _sequentialToolEnabledAssignments =
    [
        CustomLoopToolAssignment.List,
        CustomLoopToolAssignment.Read,
        CustomLoopToolAssignment.Search,
    ];

    /// <summary>
    /// Validates the complete persisted shape and cross-field invariants of a custom-loop run.
    /// </summary>
    /// <param name="run">The run to validate, or <see langword="null"/> to produce a required-value error.</param>
    /// <returns>All structural, identity, admission, context, clock, event, checkpoint, and outcome errors discovered in the run.</returns>
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
        ValidateHumanReview(run, errors);
        ValidateWaitEvidence(run, errors);
        ValidateHumanInputWaitingCheckpoints(run, errors);
        ValidateCheckpoint(run, errors);
        ValidateOutcome(run, errors);
        return new CustomLoopValidationResult(errors);
    }

    /// <summary>
    /// Validates a run for provider dispatch, including the durable admission-audit completion boundary.
    /// </summary>
    /// <param name="run">The run proposed for dispatch.</param>
    /// <returns>The complete run-validation result plus an error when the admission audit has not durably completed.</returns>
    public static CustomLoopValidationResult ValidateForDispatch(CustomLoopRunRecord? run)
    {
        var errors = Validate(run).Errors.ToList();
        if (run is not null && !HasCompleteAdmissionAudit(run))
        {
            Add(errors, "admission_audit_incomplete", "events", "Provider dispatch requires the durable admission-audit completion marker.");
        }

        return new CustomLoopValidationResult(errors);
    }

    /// <summary>
    /// Determines whether the event stream contains exactly one admission event at sequence 1 followed by a unique admission-audit completion event.
    /// </summary>
    /// <param name="run">The run whose append-only event prefix is inspected.</param>
    /// <returns><see langword="true"/> when the unique admission and audit-completion markers occupy the required sequence-1 and sequence-2 prefix; otherwise, <see langword="false"/>.</returns>
    public static bool HasCompleteAdmissionAudit(CustomLoopRunRecord? run)
    {
        if (run?.Events is not { Length: >= 2 } events)
        {
            return false;
        }

        return events[0] is { Sequence: 1, Kind: CustomLoopRunEventKind.Admitted }
            && events[1] is { Sequence: 2, Kind: CustomLoopRunEventKind.AdmissionAuditCompleted }
            && events.Count(item => item is { Kind: CustomLoopRunEventKind.Admitted }) == 1
            && events.Count(item => item is { Kind: CustomLoopRunEventKind.AdmissionAuditCompleted }) == 1;
    }

    /// <summary>
    /// Validates a proposed lifecycle update against the currently persisted run.
    /// </summary>
    /// <param name="current">The currently persisted run.</param>
    /// <param name="candidate">The proposed exact successor.</param>
    /// <returns>Errors for the candidate shape plus any terminal immutability, version, admission, lifecycle, append-only, ownership, checkpoint, clock, or timestamp regression.</returns>
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
        ValidateExecutionFrontierUpdate(current, candidate, errors);
        ValidateLifecycleTransition(current, candidate, errors);
        ValidateAppendOnlyEvents(current, candidate, errors);
        ValidateAppendOnlyHumanReview(current, candidate, errors);
        ValidateAppendOnlyWaitEvidence(current, candidate, errors);
        ValidateAppendOnlyHumanInputWaitingCheckpoints(current, candidate, errors);
        ValidateAppendedControlOwnership(current, candidate, errors);
        ValidateSequentialCheckpointAdvance(current, candidate, errors);
        ValidateMonotonicCheckpoint(current, candidate, errors);
        ValidateMonotonicExecutionClock(current, candidate, errors);
        if (candidate.UpdatedAtUtc < current.UpdatedAtUtc)
        {
            Add(errors, "updated_timestamp_regressed", "updatedAtUtc", "Updated timestamp cannot move backward.");
        }

        return new CustomLoopValidationResult(errors);
    }

    /// <summary>
    /// Determines whether two valid run records represent the exact same durable lifecycle version.
    /// </summary>
    /// <param name="expected">The previously authenticated durable record.</param>
    /// <param name="actual">The freshly loaded record.</param>
    /// <returns><see langword="true"/> only when every persisted field and append-only event is unchanged.</returns>
    public static bool HasSameDurableVersion(CustomLoopRunRecord? expected, CustomLoopRunRecord? actual)
    {
        if (expected is null
            || actual is null
            || !Validate(expected).IsValid
            || !Validate(actual).IsValid
            || expected.LifecycleVersion != actual.LifecycleVersion
            || expected.Status != actual.Status
            || expected.UpdatedAtUtc != actual.UpdatedAtUtc
            || expected.CompletedAtUtc != actual.CompletedAtUtc
            || !Equals(expected.ExecutionClock, actual.ExecutionClock)
            || !CheckpointsEqual(expected.Checkpoint, actual.Checkpoint)
            || expected.Events.Length != actual.Events.Length
            || !string.Equals(expected.FinalOutput, actual.FinalOutput, StringComparison.Ordinal)
            || !string.Equals(expected.FailureCode, actual.FailureCode, StringComparison.Ordinal)
            || !string.Equals(expected.FailureDetail, actual.FailureDetail, StringComparison.Ordinal)
            || !FrontiersEqual(expected.Frontier, actual.Frontier)
            || !HumanReviewStatesEqual(expected.HumanReview, actual.HumanReview)
            || !WaitEvidenceEqual(expected.WaitEvidence, actual.WaitEvidence)
            || !HumanInputWaitingCheckpointsEqual(expected.HumanInputWaitingCheckpoints, actual.HumanInputWaitingCheckpoints))
        {
            return false;
        }

        var immutableErrors = new List<CustomLoopValidationError>();
        ValidateImmutableAdmission(expected, actual, immutableErrors);
        if (immutableErrors.Count != 0)
        {
            return false;
        }

        for (var index = 0; index < expected.Events.Length; index++)
        {
            if (!EventsEqual(expected.Events[index], actual.Events[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Determines whether a later valid run preserves one exact immutable admission and event prefix.</summary>
    /// <param name="expectedPrefix">The authenticated earlier durable record.</param>
    /// <param name="actual">The later durable record that must preserve its complete event prefix.</param>
    /// <returns><see langword="true"/> only when both records are valid and every earlier event is byte-semantically unchanged.</returns>
    public static bool HasExactDurableEventPrefix(CustomLoopRunRecord? expectedPrefix, CustomLoopRunRecord? actual)
    {
        if (expectedPrefix is null
            || actual is null
            || !Validate(expectedPrefix).IsValid
            || !Validate(actual).IsValid
            || actual.LifecycleVersion < expectedPrefix.LifecycleVersion
            || actual.UpdatedAtUtc < expectedPrefix.UpdatedAtUtc
            || expectedPrefix.Events.Length > actual.Events.Length
            || !HasHumanReviewPrefix(expectedPrefix.HumanReview, actual.HumanReview)
            || !HasWaitEvidencePrefix(expectedPrefix.WaitEvidence, actual.WaitEvidence)
            || !HasHumanInputWaitingCheckpointPrefix(expectedPrefix.HumanInputWaitingCheckpoints, actual.HumanInputWaitingCheckpoints))
        {
            return false;
        }

        if (actual.LifecycleVersion == expectedPrefix.LifecycleVersion)
        {
            return HasSameDurableVersion(expectedPrefix, actual);
        }

        var immutableErrors = new List<CustomLoopValidationError>();
        ValidateImmutableAdmission(expectedPrefix, actual, immutableErrors);
        if (immutableErrors.Count != 0)
        {
            return false;
        }

        for (var index = 0; index < expectedPrefix.Events.Length; index++)
        {
            if (!EventsEqual(expectedPrefix.Events[index], actual.Events[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the one narrowly permitted post-terminal integrity-warning append.
    /// </summary>
    /// <param name="current">The terminal persisted run.</param>
    /// <param name="warning">The next contiguous integrity-warning event.</param>
    /// <returns>Errors when the run is nonterminal, already has the warning, lacks the terminal lifecycle boundary, or the event carries data outside the allowed warning envelope.</returns>
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
            || warning.ToolAuthority is not null || warning.ToolEvidence is not null || warning.TraceReservationUtf8Bytes is not null || warning.ControlExpectedLifecycleVersion is not null
            || warning.SequentialNodeEvidence is not null || warning.PureNodeOutcomeJson is not null || warning.WaitContinuationEvidenceHash is not null || warning.ModelExecutionEvidence is not null || warning.FailureEvidence is not null || warning.RetryState is not null)
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

    /// <summary>
    /// Determines whether a custom-loop lifecycle status may transition directly to another status.
    /// </summary>
    /// <param name="current">The persisted status.</param>
    /// <param name="next">The proposed successor status.</param>
    /// <returns><see langword="true"/> for idempotent status retention or an explicitly allowed lifecycle edge; otherwise, <see langword="false"/>.</returns>
    public static bool IsAllowedLifecycleTransition(CustomLoopRunStatus current, CustomLoopRunStatus next)
    {
        if (current == next)
        {
            return true;
        }

        return current switch
        {
            CustomLoopRunStatus.Admitted => next is CustomLoopRunStatus.Running or CustomLoopRunStatus.Paused or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.Running => next is CustomLoopRunStatus.Waiting or CustomLoopRunStatus.PauseRequested or CustomLoopRunStatus.Paused or CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.Waiting => next is CustomLoopRunStatus.Running or CustomLoopRunStatus.Paused or CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.PauseRequested => next is CustomLoopRunStatus.Paused or CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.Paused => next is CustomLoopRunStatus.Running or CustomLoopRunStatus.Waiting or CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.CancelRequested => next is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview,
            _ => false
        };
    }

    private static void ValidateIdentity(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.SchemaVersion != CustomLoopRunRecord.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_run_schema", "schemaVersion", $"Run schema version must be {CustomLoopRunRecord.CurrentSchemaVersion}. Pre-1.0 artifacts from another schema are unsupported; remove and recreate the affected development artifact.");
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
        var capabilityError = CapabilityAdmissionSnapshotValidator.Validate(run.CapabilityAdmission);
        if (capabilityError is not null)
        {
            Add(errors, "invalid_capability_admission", "capabilityAdmission", capabilityError);
        }
        else if (run.SequentialInvocationSnapshot is not null && run.SequentialAdapterBinding is not null)
        {
            ValidateSequentialCapabilityAdmission(run, errors);
        }
        else if (run.AdmittedDefinition is not null && !string.Equals(run.CapabilityAdmission.RequirementsHash, GetRequirementsHash(run.AdmittedDefinition), StringComparison.Ordinal))
        {
            Add(errors, "capability_admission_definition_mismatch", "capabilityAdmission.requirementsHash", "Admitted capability evidence must bind the admitted definition's exact requirements.");
        }

        ValidateSequentialAdmission(run, errors);
        ValidateExecutionFrontier(run, errors);

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
            var definitionValidation = run.SequentialInvocationSnapshot is not null && run.SequentialAdapterBinding is not null
                ? CustomLoopDefinitionValidator.ValidateSequentialProjection(run.AdmittedDefinition)
                : CustomLoopDefinitionValidator.Validate(run.AdmittedDefinition);
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

    private static void ValidateSequentialAdmission(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        var snapshot = run.SequentialInvocationSnapshot;
        var binding = run.SequentialAdapterBinding;
        if ((snapshot is null) != (binding is null))
        {
            Add(errors, "incomplete_sequential_binding", "sequentialAdapterBinding", "Canonical sequential runs require both the exact invocation snapshot and adapter binding; fenced legacy runs require both fields to be null.");
            return;
        }

        if (snapshot is null || binding is null)
        {
            return;
        }

        var snapshotValidation = GovernedLoopSequentialContractValidator.Validate(snapshot);
        foreach (var error in snapshotValidation.Errors)
        {
            Add(errors, "invalid_sequential_invocation_snapshot", $"sequentialInvocationSnapshot{error.Path[1..]}", "The sequential invocation snapshot is invalid.");
        }

        var bindingValidation = GovernedLoopSequentialContractValidator.Validate(binding);
        foreach (var error in bindingValidation.Errors)
        {
            Add(errors, "invalid_sequential_adapter_binding", $"sequentialAdapterBinding{error.Path[1..]}", "The sequential adapter binding is invalid.");
        }

        if (!snapshotValidation.IsValid || !bindingValidation.IsValid)
        {
            return;
        }

        var triggerDescriptor = run.Frontier?.Payload.Nodes.FirstOrDefault()?.Descriptor;
        var expectedTriggerTypeId = snapshot.TriggerOrigin is null ? "manual-trigger" : "schedule-trigger";
        if (triggerDescriptor is not
            {
                Kind: GovernedLoopNodeKind.Trigger,
                Version: 1,
            }
            || !string.Equals(triggerDescriptor.TypeId, expectedTriggerTypeId, StringComparison.Ordinal))
        {
            Add(errors, "sequential_trigger_origin_mismatch", "sequentialInvocationSnapshot.triggerOrigin", "The immutable trigger origin must exactly match the admitted frontier entry descriptor.");
        }

        if (!string.Equals(binding.InvocationPayloadHash, snapshot.ContentHash, StringComparison.Ordinal)
            || !string.Equals(binding.ExecutionBinding.RunId, run.Id, StringComparison.Ordinal)
            || !string.Equals(binding.AdmissionOperationId, run.AdmissionOperationId, StringComparison.Ordinal))
        {
            Add(errors, "sequential_binding_identity_mismatch", "sequentialAdapterBinding", "Sequential adapter coordinates must bind the exact run, invocation operation, and immutable invocation payload.");
        }

        try
        {
            if (!string.Equals(
                GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(binding.AdmissionReceipt.Evidence.CapabilityAdmission),
                GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(run.CapabilityAdmission),
                StringComparison.Ordinal))
            {
                Add(errors, "sequential_admission_capability_mismatch", "capabilityAdmission", "The durable capability resolution must exactly match the complete retained admission receipt.");
            }
        }
        catch (ArgumentException)
        {
            Add(errors, "sequential_admission_capability_mismatch", "capabilityAdmission", "The durable capability resolution must exactly match the complete retained admission receipt.");
        }

        if (!Equals(snapshot.ModelSnapshot, run.ModelSnapshot)
            || !Equals(snapshot.InvokingConversation, run.InvokingConversation)
            || !string.Equals(snapshot.TriggerPrompt, run.TriggerPrompt, StringComparison.Ordinal)
            || snapshot.ContextCapturedAtUtc != run.ContextSnapshot?.CapturedAtUtc
            || run.ContextSnapshot?.SourceManifest is null
            || !snapshot.ContextManifest.SequenceEqual(run.ContextSnapshot.SourceManifest))
        {
            Add(errors, "sequential_invocation_projection_mismatch", "sequentialInvocationSnapshot", "The sequential invocation snapshot must exactly match the legacy adapter's admitted prompt, model, conversation, and context projection.");
        }
    }

    private static void ValidateExecutionFrontier(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.Frontier is not { } frontier)
        {
            if (run.SequentialInvocationSnapshot is not null || run.SequentialAdapterBinding is not null)
            {
                Add(errors, "execution_frontier_required", "frontier", "A canonical sequential run requires its exact durable execution frontier.");
            }

            return;
        }

        var frontierValidation = GovernedLoopFrontierContractValidator.Validate(frontier);
        foreach (var error in frontierValidation.Errors)
        {
            Add(errors, "invalid_execution_frontier", "frontier" + error.Path.TrimStart('$'), "The durable execution frontier is malformed or does not match its retained content hash.");
        }

        if (run.SequentialAdapterBinding is not { } binding)
        {
            Add(errors, "execution_frontier_binding_required", "frontier", "A durable execution frontier requires the exact canonical sequential adapter binding.");
            return;
        }

        if (!Equals(frontier.Binding, binding.ExecutionBinding)
            || !string.Equals(frontier.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(frontier.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(frontier.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
            || !string.Equals(frontier.AdmissionReceiptHash, binding.AdmissionReceiptHash, StringComparison.Ordinal))
        {
            Add(errors, "execution_frontier_binding_mismatch", "frontier", "The durable execution frontier must bind the run's exact workspace, execution, graph, layout, and admission receipt coordinates.");
        }

        ValidateFrontierOutcomeEvidence(run, frontier, errors);

        if (frontier.Payload.UpdatedAtUtc < run.CreatedAtUtc || frontier.Payload.UpdatedAtUtc > run.UpdatedAtUtc)
        {
            Add(errors, "execution_frontier_timestamp_mismatch", "frontier.payload.updatedAtUtc", "The durable execution frontier timestamp must be within the retained run interval.");
        }

        if (!FrontierMatchesRunLifecycle(run, frontier))
        {
            Add(errors, "execution_frontier_lifecycle_mismatch", "frontier.payload.status", "The durable execution frontier must honestly match the retained run lifecycle.");
        }
    }

    private static void ValidateFrontierOutcomeEvidence(CustomLoopRunRecord run, GovernedLoopFrontierPosture frontier, List<CustomLoopValidationError> errors)
    {
        var runEvents = run.Events ?? [];
        for (var nodeIndex = 0; nodeIndex < frontier.Payload.Nodes.Count; nodeIndex++)
        {
            var node = frontier.Payload.Nodes[nodeIndex];
            if (node.OutcomeEvidenceId is not { } outcomeEvidenceId)
            {
                continue;
            }

            var matchingEvents = runEvents
                .Where(item => item is not null && string.Equals(item.EventId, outcomeEvidenceId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            var matchingEvent = matchingEvents.Length == 1 ? matchingEvents[0] : null;
            var evidence = matchingEvent?.SequentialNodeEvidence;
            var nodeSelectedControlEdgeIds = node.SelectedControlEdgeIds;
            var nodeSkippedControlEdgeIds = node.SkippedControlEdgeIds;
            var evidenceSelectedControlEdgeIds = evidence?.SelectedControlEdgeIds;
            var evidenceSkippedControlEdgeIds = evidence?.SkippedControlEdgeIds;
            var compatible = IsTerminalRetryFrontierEvidence(runEvents, matchingEvent, node)
                || evidence is not null
                && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
                && CustomLoopSequentialOutcomeArtifactHash.Matches(matchingEvent!)
                && string.Equals(evidence.OutcomeArtifactHash, node.OutcomeEvidenceHash, StringComparison.Ordinal)
                && evidence.ActivationOrdinal == node.ActivationOrdinal
                && evidence.VisitOrdinal == node.VisitOrdinal
                && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal)
                && string.Equals(evidence.CycleId, node.CycleId, StringComparison.Ordinal)
                && evidence.CycleIteration == node.CycleIteration
                && (node.Status == GovernedLoopNodeExecutionStatus.Skipped
                    ? evidence.Kind == CustomLoopSequentialNodeEvidenceKind.TopologySkipped
                        && evidence.Attempt is null
                        && evidence.Disposition == CustomLoopSequentialNodeDisposition.Completed
                        && matchingEvents[0].Kind == CustomLoopRunEventKind.TopologyNodeSkipped
                        && HasExactGoverningSkipActivation(frontier.Payload.Nodes, node, evidence)
                    : node.Attempt == evidence.Attempt
                        && node.ControlOutcome == evidence.ControlOutcome
                        && nodeSelectedControlEdgeIds is not null
                        && nodeSkippedControlEdgeIds is not null
                        && evidenceSelectedControlEdgeIds is not null
                        && evidenceSkippedControlEdgeIds is not null
                        && nodeSelectedControlEdgeIds.SequenceEqual(evidenceSelectedControlEdgeIds, StringComparer.Ordinal)
                        && nodeSkippedControlEdgeIds.SequenceEqual(evidenceSkippedControlEdgeIds, StringComparer.Ordinal)
                        && IsFrontierOutcomeDispositionCompatible(node.Status, evidence.Kind, evidence.Disposition));
            if (!compatible)
            {
                Add(errors, "execution_frontier_outcome_evidence_mismatch", $"frontier.payload.nodes[{nodeIndex}].outcomeEvidenceId", "Committed frontier outcome evidence must identify one exact retained run event for the same node, attempt, artifact hash, and disposition.");
            }
        }
    }

    private static bool IsTerminalRetryFrontierEvidence(
        IReadOnlyList<CustomLoopRunEvent> runEvents,
        CustomLoopRunEvent? matchingEvent,
        GovernedLoopNodeExecutionEvidence node)
    {
        if (node.Status != GovernedLoopNodeExecutionStatus.ReviewBlocked
            || matchingEvent?.Kind != CustomLoopRunEventKind.RetryStateChanged
            || matchingEvent.RetryState is not { } terminal
            || terminal.Disposition is not (GovernedLoopRetryStateDisposition.Exhausted or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview)
            || !GovernedLoopRetryContract.IsValid(terminal)
            || !string.Equals(node.OutcomeEvidenceHash, terminal.ContentHash, StringComparison.Ordinal)
            || terminal.Identity.ActivationOrdinal != node.ActivationOrdinal
            || terminal.Identity.VisitOrdinal != node.VisitOrdinal
            || !string.Equals(terminal.Identity.NodeId, node.NodeId, StringComparison.Ordinal)
            || terminal.NextAttempt is not null
            || terminal.AttemptOperationId is not null
            || node.ControlOutcome is not null
            || node.SelectedControlEdgeIds.Count != 0
            || node.SkippedControlEdgeIds.Count != 0)
        {
            return false;
        }

        var predecessors = runEvents
            .TakeWhile(candidate => !ReferenceEquals(candidate, matchingEvent))
            .Select(candidate => candidate?.RetryState)
            .Where(candidate => candidate is not null
                && string.Equals(candidate.Identity.SeriesId, terminal.Identity.SeriesId, StringComparison.Ordinal)
                && candidate.StateVersion == terminal.StateVersion - 1)
            .Take(2)
            .ToArray();
        var predecessor = predecessors.Length == 1 ? predecessors[0] : null;
        return predecessor is not null
            && predecessor.Disposition is GovernedLoopRetryStateDisposition.Scheduled or GovernedLoopRetryStateDisposition.Due
            && predecessor.NextAttempt == node.Attempt
            && string.Equals(predecessor.AttemptOperationId, node.AttemptOperationId, StringComparison.Ordinal);
    }

    private static bool IsFrontierOutcomeDispositionCompatible(
        GovernedLoopNodeExecutionStatus status,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition)
    {
        return status switch
        {
            GovernedLoopNodeExecutionStatus.Completed => kind == CustomLoopSequentialNodeEvidenceKind.CompletedOutcome && disposition == CustomLoopSequentialNodeDisposition.Completed,
            GovernedLoopNodeExecutionStatus.Failed => kind == CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection && disposition == CustomLoopSequentialNodeDisposition.Rejected,
            GovernedLoopNodeExecutionStatus.ReviewBlocked => IsClosedSequentialOutcome(kind, disposition),
            _ => false,
        };
    }

    private static bool IsClosedSequentialOutcome(CustomLoopSequentialNodeEvidenceKind kind, CustomLoopSequentialNodeDisposition disposition)
        => (kind, disposition) is
            (CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed)
            or (CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected)
            or (CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.NeedsReview);

    private static void ValidateWaitEvidence(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.WaitEvidence is null)
        {
            Add(errors, "wait_evidence_required", "waitEvidence", "The canonical activation-scoped Wait evidence collection is required, including when empty.");
            return;
        }

        if (run.WaitEvidence.Count > GovernedLoopExecutionLimits.MaxFrontierNodes)
        {
            Add(errors, "too_many_wait_evidence_items", "waitEvidence", $"A run cannot retain more than {GovernedLoopExecutionLimits.MaxFrontierNodes} Wait activations.");
        }

        var previousActivationOrdinal = -1;
        for (var index = 0; index < run.WaitEvidence.Count; index++)
        {
            var item = run.WaitEvidence[index];
            var field = $"waitEvidence[{index}]";
            if (item is null || !GovernedLoopWaitContractValidator.Validate(item).IsValid)
            {
                Add(errors, "invalid_wait_evidence", field, "Wait evidence must be a bounded, hash-valid schema-1 activation record.");
                continue;
            }

            if (item.ActivationOrdinal <= previousActivationOrdinal)
            {
                Add(errors, "unordered_wait_evidence", $"{field}.activationOrdinal", "Wait evidence must be uniquely ordered by increasing activation ordinal.");
            }

            previousActivationOrdinal = item.ActivationOrdinal;
            if (run.SequentialAdapterBinding is not { } binding
                || run.Frontier?.Payload.Nodes.ElementAtOrDefault(item.ActivationOrdinal) is not { } activation
                || activation.ActivationOrdinal != item.ActivationOrdinal
                || activation.Descriptor.Kind != GovernedLoopNodeKind.Wait
                || !Equals(activation.Descriptor, item.Condition.Descriptor)
                || !string.Equals(activation.NodeId, item.NodeId, StringComparison.Ordinal)
                || activation.VisitOrdinal != item.NodeVisitOrdinal
                || !string.Equals(activation.CycleId, item.CycleId, StringComparison.Ordinal)
                || activation.CycleIteration != item.CycleIteration
                || activation.Attempt != item.WaitAttempt
                || !string.Equals(activation.AttemptOperationId, item.WaitOperationId, StringComparison.Ordinal)
                || run.Frontier.Payload.FrontierVersion < item.ParkedFrontierVersion
                || run.Frontier.Payload.FrontierVersion == item.ParkedFrontierVersion
                    && !string.Equals(run.Frontier.Payload.ContentHash, item.ParkedFrontierHash, StringComparison.Ordinal)
                || item.ParkedAtUtc < run.CreatedAtUtc
                || item.ParkedAtUtc > run.UpdatedAtUtc)
            {
                Add(errors, "wait_evidence_frontier_mismatch", field, "Wait evidence must identify one exact Wait activation in the retained canonical frontier.");
                continue;
            }

            if (item.ParkEvidence is { } park
                && (!Equals(park.Checkpoint.Binding.Execution, binding.ExecutionBinding)
                    || !Equals(park.Checkpoint.Binding.Publication, binding.AdmissionReceipt.Intent.Publication)
                    || park.Checkpoint.PublishedAtUtc > run.UpdatedAtUtc))
            {
                Add(errors, "wait_evidence_binding_mismatch", $"{field}.parkEvidence", "Published Wait evidence must retain the run's exact execution and immutable publication binding.");
            }

            if (item.ContinuationEvidence?.ResumedAtUtc > run.UpdatedAtUtc)
            {
                Add(errors, "wait_continuation_timestamp_mismatch", $"{field}.continuationEvidence.resumedAtUtc", "Wait continuation evidence cannot postdate the retained run update.");
            }

            var compatible = activation.Status switch
            {
                GovernedLoopNodeExecutionStatus.Waiting => item.ContinuationEvidence is null
                    && activation.ControlOutcome is null
                    && activation.OutcomeEvidenceId is null
                    && activation.OutcomeEvidenceHash is null,
                GovernedLoopNodeExecutionStatus.ReviewBlocked => item.ContinuationEvidence is null,
                GovernedLoopNodeExecutionStatus.Failed => item.ContinuationEvidence is null,
                GovernedLoopNodeExecutionStatus.Running => item.ContinuationEvidence is { } continuation
                    && item.ParkEvidence is not null
                    && run.Frontier.Payload.FrontierVersion == continuation.ResumedFrontierVersion
                    && string.Equals(run.Frontier.Payload.ContentHash, continuation.ResumedFrontierHash, StringComparison.Ordinal)
                    && activation.ControlOutcome is null
                    && activation.OutcomeEvidenceId is null
                    && activation.OutcomeEvidenceHash is null,
                GovernedLoopNodeExecutionStatus.Completed => item.ContinuationEvidence is { } continuation
                    && item.ParkEvidence is not null
                    && run.Frontier.Payload.FrontierVersion > continuation.ResumedFrontierVersion
                    && activation.ControlOutcome == GovernedLoopControlCondition.Success
                    && run.Events.Count(runEvent => runEvent is not null
                        && string.Equals(runEvent.EventId, activation.OutcomeEvidenceId, StringComparison.Ordinal)
                        && string.Equals(runEvent.SequentialNodeEvidence?.OutcomeArtifactHash, activation.OutcomeEvidenceHash, StringComparison.Ordinal)
                        && string.Equals(runEvent.WaitContinuationEvidenceHash, continuation.ContentHash, StringComparison.Ordinal)) == 1,
                _ => false,
            };
            if (!compatible)
            {
                Add(errors, "wait_evidence_lifecycle_mismatch", field, "Wait evidence phase must match the exact retained activation and frontier lifecycle.");
            }
        }

        var retained = run.WaitEvidence.Where(item => item is not null).ToArray();
        var waitingActivations = run.Frontier?.Payload.Nodes
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Wait && node.Status == GovernedLoopNodeExecutionStatus.Waiting)
            .ToArray() ?? [];
        var completedActivations = run.Frontier?.Payload.Nodes
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Wait && node.Status == GovernedLoopNodeExecutionStatus.Completed)
            .ToArray() ?? [];
        var retryWaitingActivations = run.Frontier?.Payload.Nodes
            .Where(node => node.Status == GovernedLoopNodeExecutionStatus.Waiting && HasExactWaitingRetry(run, node))
            .ToArray() ?? [];
        if (waitingActivations.Any(node => retained.Count(item => item.ActivationOrdinal == node.ActivationOrdinal) != 1))
        {
            Add(errors, "waiting_run_evidence_required", "waitEvidence", "Every Waiting frontier activation requires exactly one activation-scoped Wait evidence record, regardless of aggregate lifecycle status.");
        }

        if (completedActivations.Any(node => retained.Count(item => item.ActivationOrdinal == node.ActivationOrdinal
            && item.ParkEvidence is not null
            && item.ContinuationEvidence is not null) != 1))
        {
            Add(errors, "completed_wait_evidence_required", "waitEvidence", "Every completed Wait activation requires exactly one retained park and continuation evidence chain.");
        }

        var humanInputWaitingActivations = run.Frontier?.Payload.Nodes
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanInput && node.Status == GovernedLoopNodeExecutionStatus.Waiting)
            .ToArray() ?? [];
        if (run.Status == CustomLoopRunStatus.Waiting && waitingActivations.Length == 0 && retryWaitingActivations.Length == 0 && humanInputWaitingActivations.Length == 0)
        {
            Add(errors, "waiting_run_evidence_required", "waitEvidence", "A Waiting run requires at least one exact Wait, Human Input, or governed-retry checkpoint binding.");
        }
    }

    private static void ValidateHumanInputWaitingCheckpoints(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (run.HumanInputWaitingCheckpoints is null)
        {
            Add(errors, "human_input_waiting_checkpoints_required", "humanInputWaitingCheckpoints", "The canonical Human Input waiting-checkpoint collection is required, including when empty.");
            return;
        }

        if (run.HumanInputWaitingCheckpoints.Count > GovernedLoopExecutionLimits.MaxFrontierNodes)
        {
            Add(errors, "too_many_human_input_waiting_checkpoints", "humanInputWaitingCheckpoints", $"A run cannot retain more than {GovernedLoopExecutionLimits.MaxFrontierNodes} Human Input checkpoints.");
        }

        var checkpointIds = new HashSet<string>(StringComparer.Ordinal);
        var requestIdentities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < run.HumanInputWaitingCheckpoints.Count; index++)
        {
            var checkpoint = run.HumanInputWaitingCheckpoints[index];
            var field = $"humanInputWaitingCheckpoints[{index}]";
            if (checkpoint is null || !GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(checkpoint).IsValid)
            {
                Add(errors, "invalid_human_input_waiting_checkpoint", field, "Human Input checkpoints must be complete, hash-valid schema-1 data-only waiting contracts.");
                continue;
            }

            var binding = run.SequentialAdapterBinding;
            var checkpointBinding = checkpoint.Binding;
            var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpointBinding.ActivationOrdinal);
            var requestIdentity = checkpoint.Request.RequestId + "\n" + checkpoint.Request.RequestVersionId;
            if (!checkpointIds.Add(checkpointBinding.CheckpointId) || !requestIdentities.Add(requestIdentity))
            {
                Add(errors, "duplicate_human_input_waiting_checkpoint", field, "Each Human Input checkpoint and request version identity may occur only once in one run.");
            }

            if (binding is null
                || run.Frontier is null
                || !Equals(checkpointBinding.Execution, binding.ExecutionBinding)
                || !Equals(checkpointBinding.Publication, binding.AdmissionReceipt.Intent.Publication)
                || !string.Equals(checkpointBinding.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
                || !string.Equals(checkpointBinding.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
                || !string.Equals(checkpointBinding.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
                || !string.Equals(checkpointBinding.AdmissionReceiptHash, binding.AdmissionReceiptHash, StringComparison.Ordinal)
                || !string.Equals(checkpoint.ResolvedPolicy.ActorId, run.AdmissionActor, StringComparison.Ordinal)
                || activation is null
                || activation.Descriptor.Kind != GovernedLoopNodeKind.HumanInput
                || !string.Equals(activation.NodeId, checkpointBinding.NodeId, StringComparison.Ordinal)
                || activation.VisitOrdinal != checkpointBinding.NodeVisitOrdinal
                || !string.Equals(activation.CycleId, checkpointBinding.CycleId, StringComparison.Ordinal)
                || activation.CycleIteration != checkpointBinding.CycleIteration
                || (checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Pending or GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed)
                    && activation.Status != GovernedLoopNodeExecutionStatus.Waiting
                || checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Expired
                    && activation.Status != GovernedLoopNodeExecutionStatus.Failed
                || checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Rejected
                    && activation.Status != GovernedLoopNodeExecutionStatus.Failed
                || checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled
                    && activation.Status != GovernedLoopNodeExecutionStatus.Waiting
                || checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.NeedsReview
                    && activation.Status != GovernedLoopNodeExecutionStatus.ReviewBlocked
                || checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Terminal
                    && activation.Status != GovernedLoopNodeExecutionStatus.Completed
                || run.Frontier.Payload.FrontierVersion < checkpointBinding.FrontierVersion
                || run.Frontier.Payload.FrontierVersion == checkpointBinding.FrontierVersion
                    && !string.Equals(run.Frontier.Payload.ContentHash, checkpointBinding.FrontierHash, StringComparison.Ordinal)
                || checkpoint.Request.Timing.RequestedAtUtc < run.CreatedAtUtc
                || checkpoint.Request.Timing.RequestedAtUtc > run.UpdatedAtUtc)
            {
                Add(errors, "human_input_waiting_checkpoint_binding_mismatch", field, "A retained Human Input checkpoint must bind one exact admitted Human Input activation, generation, publication, and frontier history.");
            }
        }

        var waitingHumanInputActivations = run.Frontier?.Payload.Nodes
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanInput && node.Status == GovernedLoopNodeExecutionStatus.Waiting)
            .ToArray() ?? [];
        if (waitingHumanInputActivations.Any(activation => run.HumanInputWaitingCheckpoints.Count(checkpoint => checkpoint is not null
            && checkpoint.Binding.ActivationOrdinal == activation.ActivationOrdinal) != 1))
        {
            Add(errors, "human_input_waiting_checkpoint_required", "humanInputWaitingCheckpoints", "Every durable Waiting Human Input activation requires exactly one retained checkpoint.");
        }
    }

    private static bool HasExactWaitingRetry(CustomLoopRunRecord run, GovernedLoopNodeExecutionEvidence activation)
    {
        var matches = run.Events.Select(item => item?.RetryState)
            .Where(state => state is not null
                && state.Identity.ActivationOrdinal == activation.ActivationOrdinal
                && state.Identity.VisitOrdinal == activation.VisitOrdinal
                && string.Equals(state.Identity.NodeId, activation.NodeId, StringComparison.Ordinal))
            .OrderByDescending(state => state!.StateVersion)
            .ThenByDescending(state => state!.RecordedAtUtc)
            .FirstOrDefault();
        return matches is
        {
            Disposition: GovernedLoopRetryStateDisposition.Scheduled,
        }
            && matches.NextAttempt == activation.Attempt
            && string.Equals(matches.AttemptOperationId, activation.AttemptOperationId, StringComparison.Ordinal);
    }

    private static void ValidateExecutionFrontierUpdate(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if ((current.Frontier is null) != (candidate.Frontier is null))
        {
            Add(errors, "execution_frontier_presence_changed", "frontier", "A run update cannot add or remove its durable execution-frontier plane.");
            return;
        }

        if (current.Frontier is not { } currentFrontier || candidate.Frontier is not { } candidateFrontier)
        {
            return;
        }

        if (string.Equals(currentFrontier.Payload.ContentHash, candidateFrontier.Payload.ContentHash, StringComparison.Ordinal))
        {
            return;
        }

        var transition = GovernedLoopExecutionValidator.ValidateTransition(currentFrontier, candidateFrontier);
        foreach (var error in transition.Errors)
        {
            Add(errors, "invalid_execution_frontier_transition", "frontier" + error.Path.TrimStart('$'), "The durable execution frontier is not the exact legal successor of the retained frontier.");
        }
    }

    private static bool FrontierMatchesRunLifecycle(CustomLoopRunRecord run, GovernedLoopFrontierPosture frontier)
    {
        var status = run.Status;
        var frontierStatus = frontier.Payload.Status;
        return status switch
        {
            CustomLoopRunStatus.Admitted or CustomLoopRunStatus.Running => frontierStatus == GovernedLoopFrontierStatus.Active,
            CustomLoopRunStatus.Waiting => frontierStatus == GovernedLoopFrontierStatus.Waiting,
            CustomLoopRunStatus.PauseRequested or CustomLoopRunStatus.CancelRequested => frontierStatus is GovernedLoopFrontierStatus.Active or GovernedLoopFrontierStatus.Waiting or GovernedLoopFrontierStatus.ReviewBlocked,
            CustomLoopRunStatus.Paused => frontierStatus is GovernedLoopFrontierStatus.Waiting or GovernedLoopFrontierStatus.ReviewBlocked
                || frontierStatus == GovernedLoopFrontierStatus.Active
                    && (frontier.Payload.Nodes.All(node => node.Status != GovernedLoopNodeExecutionStatus.Running)
                        || AllRunningAttemptsAreRestartSafe(run, frontier)),
            CustomLoopRunStatus.Completed => frontierStatus == GovernedLoopFrontierStatus.Completed,
            CustomLoopRunStatus.Failed => frontierStatus == GovernedLoopFrontierStatus.Failed,
            CustomLoopRunStatus.Cancelled => frontierStatus == GovernedLoopFrontierStatus.Cancelled,
            CustomLoopRunStatus.NeedsReview => frontierStatus == GovernedLoopFrontierStatus.ReviewBlocked,
            _ => false,
        };
    }

    private static bool AllRunningAttemptsAreRestartSafe(CustomLoopRunRecord run, GovernedLoopFrontierPosture frontier)
    {
        var runningNodes = frontier.Payload.Nodes
            .Where(node => node.Status == GovernedLoopNodeExecutionStatus.Running)
            .ToArray();
        return runningNodes.Length > 0
            && runningNodes.All(node => HasRestartSafePureRunningAttempt(run, node)
                || HasRestartSafeRecoverableActionRunningAttempt(run, node)
                || HasRestartSafeFailRunningAttempt(run, node)
                || HasAuthenticatedTerminalRunningAttempt(run, node));
    }

    private static bool HasRestartSafePureRunningAttempt(CustomLoopRunRecord run, GovernedLoopNodeExecutionEvidence node)
    {
        if (node is not
            {
                Status: GovernedLoopNodeExecutionStatus.Running,
                Attempt: not null,
                AttemptOperationId: not null,
                Descriptor.Kind: GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate,
            })
        {
            return false;
        }

        var starts = ExactRunningAttemptStarts(run, node)
            .Where(item => item is
            {
                TraceReservationUtf8Bytes: CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
            })
            .Take(2)
            .ToArray();
        return starts.Length == 1;
    }

    private static bool HasRestartSafeRecoverableActionRunningAttempt(CustomLoopRunRecord run, GovernedLoopNodeExecutionEvidence node)
    {
        if (node is not
            {
                Status: GovernedLoopNodeExecutionStatus.Running,
                Attempt: not null,
                AttemptOperationId: not null,
                Descriptor.Kind: GovernedLoopNodeKind.Action,
            }
            || !(WorkspaceActionNodeDescriptors.TryResolve(node.Descriptor, out _)
                || CommandActionNodeDescriptors.IsCommandAction(node.Descriptor)))
        {
            return false;
        }

        var starts = ExactRunningAttemptStarts(run, node)
            .Where(item => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes)
            .Take(2)
            .ToArray();
        return starts.Length == 1;
    }

    private static bool HasRestartSafeFailRunningAttempt(CustomLoopRunRecord run, GovernedLoopNodeExecutionEvidence node)
    {
        if (node is not
            {
                Status: GovernedLoopNodeExecutionStatus.Running,
                Attempt: not null,
                AttemptOperationId: not null,
                Descriptor.Kind: GovernedLoopNodeKind.Fail,
            })
        {
            return false;
        }

        var starts = ExactRunningAttemptStarts(run, node)
            .Where(item => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes)
            .Take(2)
            .ToArray();
        return starts.Length == 1;
    }

    private static bool HasAuthenticatedTerminalRunningAttempt(CustomLoopRunRecord run, GovernedLoopNodeExecutionEvidence node)
    {
        var starts = ExactRunningAttemptStarts(run, node).Take(2).ToArray();
        if (starts.Length != 1 || starts[0].SequentialNodeEvidence is not { } dispatch)
        {
            return false;
        }

        var started = starts[0];
        var terminals = run.Events.Where(item => item.Sequence > started.Sequence
            && item.SequentialNodeEvidence is { } outcome
            && item.Iteration == started.Iteration
            && string.Equals(item.StepId, started.StepId, StringComparison.Ordinal)
            && item.Attempt == started.Attempt
            && SameSequentialBinding(outcome, dispatch)
            && string.Equals(outcome.NodeId, dispatch.NodeId, StringComparison.Ordinal)
            && outcome.Attempt == dispatch.Attempt
            && IsResolvedSequentialOutcome(item.Kind, outcome)
            && CustomLoopSequentialNodeEvidenceHash.Matches(outcome)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item))
            .Take(2)
            .ToArray();
        return terminals.Length == 1;
    }

    private static IEnumerable<CustomLoopRunEvent> ExactRunningAttemptStarts(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence node)
    {
        if (node is not
            {
                Status: GovernedLoopNodeExecutionStatus.Running,
                Attempt: { } attempt,
                AttemptOperationId: { } attemptOperationId,
            }
            || run.SequentialAdapterBinding is not { } binding)
        {
            return [];
        }

        return run.Events.Where(item => item.Sequence > run.Checkpoint.LastCommittedSequence
            && item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted
            && item.Iteration is > 0
            && item.Attempt == attempt
            && string.Equals(item.EventId, attemptOperationId, StringComparison.Ordinal)
            && StepIdMatchesNode(item.StepId, node)
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            } evidence
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && SequentialBindingMatchesRun(evidence, run, binding)
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item));
    }

    private static bool StepIdMatchesNode(string? stepId, GovernedLoopNodeExecutionEvidence node)
        => string.Equals(
            stepId,
            node.Descriptor.Kind == GovernedLoopNodeKind.Exit ? "exit" : node.NodeId,
            StringComparison.Ordinal);

    private static bool SequentialBindingMatchesRun(
        CustomLoopSequentialNodeEvidence evidence,
        CustomLoopRunRecord run,
        GovernedLoopSequentialAdapterBinding binding)
        => string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(evidence.RunId, run.Id, StringComparison.Ordinal)
            && Equals(evidence.Revision, binding.ExecutionBinding.Revision)
            && evidence.ExecutionGeneration == binding.ExecutionBinding.ExecutionGeneration;

    private static bool SameSequentialBinding(
        CustomLoopSequentialNodeEvidence candidate,
        CustomLoopSequentialNodeEvidence expected)
        => string.Equals(candidate.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(candidate.RunId, expected.RunId, StringComparison.Ordinal)
            && Equals(candidate.Revision, expected.Revision)
            && candidate.ExecutionGeneration == expected.ExecutionGeneration;

    private static bool IsResolvedSequentialOutcome(
        CustomLoopRunEventKind eventKind,
        CustomLoopSequentialNodeEvidence evidence)
        => (eventKind, evidence.Kind, evidence.Disposition) is
            (CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.ExitDecisionCompleted,
                CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                CustomLoopSequentialNodeDisposition.Completed)
            or (CustomLoopRunEventKind.NodeAttemptFailed,
                CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                CustomLoopSequentialNodeDisposition.Rejected);

    private static void ValidateSequentialCapabilityAdmission(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        var binding = run.SequentialAdapterBinding!;
        var capabilityAdmission = run.CapabilityAdmission;
        var toolAssignments = run.AdmittedDefinition?.ToolAssignments;
        var hasInference = run.AdmittedDefinition?.InferenceSteps.Length > 0;
        var hasWorkspaceCommandAdmission = capabilityAdmission.Evidence.Any(item => item.SubjectId.Equals(capabilityAdmission.Requirements.SubjectId)
            && string.Equals(item.Outcome, "Selected", StringComparison.Ordinal)
            && string.Equals(item.SelectedIdentity?.Id.Value, "org.embodysense/workspace-command", StringComparison.Ordinal));
        string[] expectedRootIdentities;
        var scheduled = run.SequentialInvocationSnapshot?.TriggerOrigin is not null;
        if (toolAssignments is { Length: 0 })
        {
            expectedRootIdentities = hasInference
                ? scheduled
                    ? _sequentialScheduledToolFreeCapabilityRootIds
                    : _sequentialToolFreeCapabilityRootIds
                : scheduled
                    ? ["org.embodysense/conversation-turn", "org.embodysense/triggers/time"]
                    : ["org.embodysense/conversation-turn"];
        }
        else if (hasInference && toolAssignments is not null && toolAssignments.SequenceEqual(_sequentialToolEnabledAssignments))
        {
            expectedRootIdentities = scheduled
                ? _sequentialScheduledToolEnabledCapabilityRootIds
                : _sequentialToolEnabledCapabilityRootIds;
        }
        else
        {
            Add(errors, "sequential_tool_assignment_mismatch", "admittedDefinition.toolAssignments", "Canonical sequential execution supports either no tools or exactly the ordered List, Read, and Search assignment catalog.");
            return;
        }
        if (hasWorkspaceCommandAdmission && !expectedRootIdentities.Contains("org.embodysense/workspace-command", StringComparer.Ordinal))
        {
            expectedRootIdentities = [.. expectedRootIdentities, "org.embodysense/workspace-command"];
        }
        var routedProfileIds = binding.AdmissionReceipt.Evidence.ModelRoutingAdmission.Entries
            .SelectMany(entry => entry.Fallbacks.Prepend(entry.Primary))
            .Select(profile => profile.Capability.DescriptorIdentity.Id.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!hasInference && routedProfileIds.Length > 0)
        {
            Add(errors, "sequential_model_routing_without_inference", "capabilityAdmission.evidence.modelRoutingAdmission", "A canonical graph without an Inference node cannot retain model-routing admission evidence.");
        }
        expectedRootIdentities = expectedRootIdentities
            .Concat(hasInference ? routedProfileIds : [])
            .Concat(binding.CommandActionCapabilityIds)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expectedGraphChecksum = "sha256:" + binding.GraphArtifactHash;
        if (!string.Equals(capabilityAdmission.Requirements.Artifact.Checksum?.Value, expectedGraphChecksum, StringComparison.Ordinal))
        {
            Add(errors, "sequential_capability_graph_artifact_mismatch", "capabilityAdmission.requirements.artifact.checksum", "Canonical sequential capability evidence must bind the adapter's exact graph artifact hash.");
        }

        var selectedRootIdentities = capabilityAdmission.Evidence
            .Where(item => item.SubjectId.Equals(capabilityAdmission.Requirements.SubjectId)
                && string.Equals(item.Outcome, "Selected", StringComparison.Ordinal))
            .Select(item => item.SelectedIdentity?.Id.Value ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!selectedRootIdentities.SequenceEqual(expectedRootIdentities, StringComparer.Ordinal))
        {
            Add(errors, "sequential_capability_identity_mismatch", "capabilityAdmission.evidence", $"Canonical sequential execution requires exactly the sorted roots derived from its trigger origin, closed inference-tool shape, and exact recoverable Action descriptors (expected `{string.Join(',', expectedRootIdentities)}`; observed `{string.Join(',', selectedRootIdentities)}`).");
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

        if (run.Status is CustomLoopRunStatus.Admitted or CustomLoopRunStatus.Waiting or CustomLoopRunStatus.Paused or CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview && run.ExecutionClock.ActiveSinceUtc is not null)
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

        var currentWorkspaceSources = new[]
        {
            (Id: "nearest-agents", PathSuffix: "AGENTS.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "role", PathSuffix: ".agent/ROLE.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "soul", PathSuffix: ".agent/SOUL.md", Source: CustomLoopContextSource.AgentIdentity, Provenance: CustomLoopContextProvenance.WorkspaceAgentIdentityFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "personality", PathSuffix: ".agent/PERSONALITY.md", Source: CustomLoopContextSource.AgentIdentity, Provenance: CustomLoopContextProvenance.WorkspaceAgentIdentityFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "context", PathSuffix: ".agent/CONTEXT.md", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User),
            (Id: "memory", PathSuffix: ".agent/MEMORY.md", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User),
            (Id: "models", PathSuffix: ".agent/models.json", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User)
        };
        var expectedWorkspaceSources = currentWorkspaceSources;
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
                Add(errors, "unsupported_manifest_source_type", $"{field}.sourceType", "Admission manifest may contain only role instruction, agent identity, contextual state, and invoking-conversation sources.");
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
                    Add(errors, "invalid_workspace_context_classification", field, "Workspace sources must preserve the designated current role, identity, contextual-state order, and trust classification.");
                }
            }
            else if (source.SourceType != CustomLoopContextSource.InvokingConversation || source.Provenance != CustomLoopContextProvenance.LogicalConversation || source.TrustClass != CustomLoopContextTrustClass.UntrustedData || source.Role != LlmMessageRole.User)
            {
                Add(errors, "invalid_conversation_context_classification", field, "Sources after the seven workspace entries must be lower-authority logical invoking-conversation data.");
            }

            if (source.SourceType is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.AgentIdentity or CustomLoopContextSource.ContextualState && source.UsedCharacterCount > CustomLoopLimits.MaxInstructionCharacters)
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

        if (run.SequentialInvocationSnapshot is not null
            && run.SequentialAdapterBinding is not null
            && run.Events[0]?.SequentialNodeEvidence is null)
        {
            Add(errors, "sequential_trigger_evidence_required", "events[0].sequentialNodeEvidence", "Canonical sequential materialization requires the initial admitted event to retain its exact completed Trigger evidence.");
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
        var sequentialStarts = new HashSet<(int ActivationOrdinal, int Attempt)>();
        var sequentialTerminals = new HashSet<(int ActivationOrdinal, int Attempt)>();
        var latestSequentialVisits = new Dictionary<string, int>(StringComparer.Ordinal);
        var retrySeriesByActivation = new Dictionary<(int ActivationOrdinal, int VisitOrdinal), string>();
        var latestRetryStateBySeries = new Dictionary<string, GovernedLoopRetryState>(StringComparer.Ordinal);
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
            var detailLimit = item.Kind switch
            {
                CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning => CustomLoopLimits.MaxLifecycleControlDetailCharacters,
                CustomLoopRunEventKind.RetryStateChanged => CustomLoopLimits.MaxRetryStateDetailCharacters,
                _ => CustomLoopLimits.MaxRunDetailCharacters,
            };
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
            ValidatePureNodeOutcome(item, field, run, errors);
            ValidateSequentialNodeEvidence(item, index, field, run, sequentialStarts, sequentialTerminals, latestSequentialVisits, errors);
            ValidateFailureEvidence(item, field, run, errors);
            ValidateRetryState(item, index, field, run, retrySeriesByActivation, latestRetryStateBySeries, errors);
            ValidateWaitContinuationEvent(item, field, run, errors);
            ValidateModelExecutionEvidence(item, field, run, errors);
            ValidateTraceReservation(item, field, run, errors);
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

        ValidateIntegrityReservationScope(run.Events, errors);
        if (run.Events[0] is { Sequence: 1, Kind: not CustomLoopRunEventKind.Admitted })
        {
            Add(errors, "first_event_not_admission", "events[0].kind", "The first run event must be the admission event.");
        }

        if (run.Events.Count(item => item is { Kind: CustomLoopRunEventKind.Admitted }) > 1)
        {
            Add(errors, "duplicate_admission_event", "events", "A run may retain exactly one admission event.");
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
                || marker.Provider is not null || marker.Model is not null || marker.ProviderResponseId is not null || marker.ExitDecision is not null || marker.ToolAuthority is not null || marker.ToolEvidence is not null || marker.TraceReservationUtf8Bytes is not null || marker.ControlExpectedLifecycleVersion is not null
                || marker.SequentialNodeEvidence is not null || marker.PureNodeOutcomeJson is not null || marker.WaitContinuationEvidenceHash is not null || marker.ModelExecutionEvidence is not null || marker.FailureEvidence is not null || marker.RetryState is not null)
            {
                Add(errors, "invalid_admission_audit_marker", $"events[{markerIndex}]", "The admission-audit completion marker cannot carry prompt, output, provider, publication, or node-attempt data.");
            }
        }
    }

    private static void ValidatePureNodeOutcome(CustomLoopRunEvent item, string field, CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        if (item.PureNodeOutcomeJson is not { } outcomeJson)
        {
            return;
        }

        if (outcomeJson.Length == 0)
        {
            Add(errors, "invalid_pure_node_outcome_json", $"{field}.pureNodeOutcomeJson", "A retained pure-node outcome must contain bounded canonical JSON.");
        }
        else
        {
            try
            {
                if (outcomeJson.Length > CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes
                    || _strictUtf8.GetByteCount(outcomeJson) > CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes)
                {
                    Add(errors, "pure_node_outcome_too_large", $"{field}.pureNodeOutcomeJson", $"A retained pure-node outcome cannot exceed {CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes} UTF-8 bytes.");
                }
            }
            catch (EncoderFallbackException)
            {
                Add(errors, "invalid_pure_node_outcome_json", $"{field}.pureNodeOutcomeJson", "A retained pure-node outcome must be valid UTF-16 text that can be encoded as strict UTF-8.");
            }
        }

        if (item.Kind != CustomLoopRunEventKind.NodeAttemptCompleted
            || item.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            }
            || !IsPureNodeEvent(run, item))
        {
            Add(errors, "invalid_pure_node_outcome_coupling", $"{field}.pureNodeOutcomeJson", "Pure-node outcome JSON belongs only to an exact completed Transform or Validate node attempt; canonical graph and outcome verification occurs at the Application boundary.");
        }

        if (item.ContextBlocks is not { Length: 0 }
            || item.CanonicalOutput is not null
            || item.OriginalOutputCharacterCount is not null
            || item.CanonicalOutputTruncated is not null
            || item.RetainedForLoopReasoning is not null
            || item.PublishedToInvokingConversation is not null
            || item.ConversationPublicationId is not null
            || item.Provider is not null
            || item.Model is not null
            || item.ProviderResponseId is not null
            || item.ExitDecision is not null
            || item.ToolAuthority is not null
            || item.ToolEvidence is not null
            || item.TraceReservationUtf8Bytes is not null
            || item.ControlExpectedLifecycleVersion is not null)
        {
            Add(errors, "invalid_pure_node_outcome_payload", field, "A pure-node completion cannot carry provider, context, publication, exit, tool, control, or legacy model-output payload.");
        }
    }

    private static void ValidateWaitContinuationEvent(
        CustomLoopRunEvent item,
        string field,
        CustomLoopRunRecord run,
        List<CustomLoopValidationError> errors)
    {
        if (item.WaitContinuationEvidenceHash is not { } continuationHash)
        {
            return;
        }

        ValidateHash(continuationHash, $"{field}.waitContinuationEvidenceHash", errors);
        if (item is not
            {
                Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
                SequentialNodeEvidence:
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                    Disposition: CustomLoopSequentialNodeDisposition.Completed,
                    ControlOutcome: GovernedLoopControlCondition.Success,
                } sequential,
            }
            || run.Frontier?.Payload.Nodes.ElementAtOrDefault(sequential.ActivationOrdinal)?.Descriptor.Kind != GovernedLoopNodeKind.Wait
            || run.WaitEvidence?.Count(wait => wait is not null
                && wait.ActivationOrdinal == sequential.ActivationOrdinal
                && string.Equals(wait.ContinuationEvidence?.ContentHash, continuationHash, StringComparison.Ordinal)) != 1)
        {
            Add(errors, "invalid_wait_continuation_event", $"{field}.waitContinuationEvidenceHash", "Only the exact completed Wait event may carry the retained activation's continuation evidence hash.");
        }
    }

    private static void ValidateSequentialNodeEvidence(
        CustomLoopRunEvent item,
        int eventIndex,
        string field,
        CustomLoopRunRecord run,
        HashSet<(int ActivationOrdinal, int Attempt)> starts,
        HashSet<(int ActivationOrdinal, int Attempt)> terminals,
        Dictionary<string, int> latestVisits,
        List<CustomLoopValidationError> errors)
    {
        if (item.SequentialNodeEvidence is not { } evidence)
        {
            return;
        }

        if (run.SequentialAdapterBinding is not { } binding)
        {
            Add(errors, "unexpected_sequential_node_evidence", $"{field}.sequentialNodeEvidence", "Sequential node evidence requires an exact run-level sequential adapter binding.");
            return;
        }

        var validKind = Enum.IsDefined(evidence.Kind) && evidence.Kind != CustomLoopSequentialNodeEvidenceKind.Unknown;
        var validDisposition = Enum.IsDefined(evidence.Disposition)
            && evidence.Disposition == (evidence.Kind switch
            {
                CustomLoopSequentialNodeEvidenceKind.DispatchStarted => CustomLoopSequentialNodeDisposition.Unknown,
                CustomLoopSequentialNodeEvidenceKind.CompletedOutcome => CustomLoopSequentialNodeDisposition.Completed,
                CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection => CustomLoopSequentialNodeDisposition.Rejected,
                CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention => CustomLoopSequentialNodeDisposition.NeedsReview,
                CustomLoopSequentialNodeEvidenceKind.TopologySkipped => CustomLoopSequentialNodeDisposition.Completed,
                _ => (CustomLoopSequentialNodeDisposition)(-1),
            });
        if (evidence.SchemaVersion != CustomLoopSequentialNodeEvidence.CurrentSchemaVersion
            || !validKind
            || !validDisposition
            || evidence.ActivationOrdinal is < 0 or >= EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxFrontierNodes
            || evidence.VisitOrdinal is < 1 or > EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxNodeVisits
            || !CustomLoopArtifactIdentifier.IsValid(evidence.NodeId)
            || evidence.Attempt is not null and (< 1 or > EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxNodeAttempt)
            || (evidence.CycleId is null) != (evidence.CycleIteration is null)
            || evidence.CycleId is not null && !CustomLoopArtifactIdentifier.IsValid(evidence.CycleId)
            || evidence.CycleIteration is not null and (< 1 or > EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxCycleIterations)
            || !HasValidSequentialRouteShape(evidence)
            || !CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            || !CustomLoopSequentialOutcomeArtifactHash.Matches(item))
        {
            Add(errors, "invalid_sequential_node_evidence", $"{field}.sequentialNodeEvidence", "Sequential node evidence has an unsupported schema, identity, disposition, outcome digest, or canonical evidence hash.");
            return;
        }

        var execution = binding.ExecutionBinding;
        if (!string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(evidence.RunId, run.Id, StringComparison.Ordinal)
            || !Equals(evidence.Revision, execution.Revision)
            || evidence.ExecutionGeneration != execution.ExecutionGeneration
            || item.Attempt is not null && item.Attempt != evidence.Attempt)
        {
            Add(errors, "sequential_node_evidence_binding_mismatch", $"{field}.sequentialNodeEvidence", "Sequential node evidence must match the exact run binding and containing event attempt.");
        }

        ValidateSequentialNodeCoordinates(item, eventIndex, evidence, field, run, errors);
        ValidateSequentialEvidenceFrontierCoordinates(item, evidence, field, run, errors);

        if (evidence.Kind == CustomLoopSequentialNodeEvidenceKind.DispatchStarted)
        {
            if (item.Kind is not (CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted))
            {
                Add(errors, "invalid_sequential_dispatch_marker", $"{field}.kind", "A sequential dispatch-start marker belongs only to a durable provider-attempt start event.");
            }

            var attempt = evidence.Attempt.GetValueOrDefault();
            if (evidence.Attempt is null
                || attempt > 1 && !HasPriorRetryDispatch(run.Events, eventIndex, evidence)
                || !starts.Add((evidence.ActivationOrdinal, attempt)))
            {
                Add(errors, "invalid_sequential_node_attempt", $"{field}.sequentialNodeEvidence.attempt", "A dispatched activation must retain exactly one start marker; attempts after one require an earlier exact durable retry-dispatch reservation.");
            }

            if (attempt == 1)
            {
                RegisterSequentialVisit(evidence, field, latestVisits, errors);
            }
            else if (latestVisits.GetValueOrDefault(evidence.NodeId) != evidence.VisitOrdinal)
            {
                Add(errors, "retry_visit_substituted", $"{field}.sequentialNodeEvidence.visitOrdinal", "A retry must preserve the original activation visit ordinal.");
            }
            return;
        }

        if (evidence.Kind == CustomLoopSequentialNodeEvidenceKind.TopologySkipped)
        {
            if (item.Kind != CustomLoopRunEventKind.TopologyNodeSkipped
                || item.Attempt is not null
                || evidence.Attempt is not null)
            {
                Add(errors, "invalid_sequential_skip_marker", $"{field}.sequentialNodeEvidence", "Topology-pruning evidence belongs only to one undispatched skip event without attempt coordinates.");
            }

            RegisterSequentialVisit(evidence, field, latestVisits, errors);
            return;
        }

        if (evidence.Attempt is not { } terminalAttempt)
        {
            Add(errors, "sequential_terminal_attempt_required", $"{field}.sequentialNodeEvidence.attempt", "A dispatched terminal outcome requires its exact positive retry attempt.");
            return;
        }

        var key = (evidence.ActivationOrdinal, terminalAttempt);

        var compatibleTerminalEvent = evidence.Kind switch
        {
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome => item.Kind is CustomLoopRunEventKind.Admitted or CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.ExitDecisionCompleted,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed,
            CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention => item.Kind is CustomLoopRunEventKind.NodeAttemptFailed or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.ExitDecisionCompleted,
            _ => false,
        };
        if (!compatibleTerminalEvent)
        {
            Add(errors, "invalid_sequential_terminal_marker", $"{field}.kind", "The terminal sequential evidence disposition is incompatible with the selected durable admission, completion, observed-outcome, failure, or Exit event.");
        }

        if (!terminals.Add(key))
        {
            Add(errors, "duplicate_sequential_node_outcome", $"{field}.sequentialNodeEvidence", "A canonical node attempt may retain exactly one terminal evidence record.");
        }

        if (item.Kind == CustomLoopRunEventKind.Admitted)
        {
            if (evidence.Kind != CustomLoopSequentialNodeEvidenceKind.CompletedOutcome
                || evidence.ActivationOrdinal != 0
                || evidence.VisitOrdinal != 1
                || terminalAttempt != 1
                || latestVisits.ContainsKey(evidence.NodeId))
            {
                Add(errors, "invalid_sequential_trigger_outcome", $"{field}.sequentialNodeEvidence", "The admission event may retain only the first completed Manual Trigger outcome.");
            }
            else
            {
                latestVisits[evidence.NodeId] = evidence.VisitOrdinal;
            }
        }
        else if (!starts.Contains(key)
            && !HasExactRetryExhaustionWithoutDispatch(run.Events, eventIndex, item, evidence)
            && !HasExactHumanInputTerminalWithoutDispatch(run, item, evidence))
        {
            Add(errors, "sequential_dispatch_marker_required", $"{field}.sequentialNodeEvidence", "Terminal provider-node evidence requires an earlier exact dispatch-start marker for the same canonical attempt.");
        }
    }

    private static bool HasExactHumanInputTerminalWithoutDispatch(
        CustomLoopRunRecord run,
        CustomLoopRunEvent item,
        CustomLoopSequentialNodeEvidence evidence)
        => HasExactAcceptedHumanInputTerminalWithoutDispatch(run, item, evidence)
            || HasExactNoResponseHumanInputTerminalWithoutDispatch(run, item, evidence);

    private static bool HasExactAcceptedHumanInputTerminalWithoutDispatch(
        CustomLoopRunRecord run,
        CustomLoopRunEvent item,
        CustomLoopSequentialNodeEvidence evidence)
    {
        if (item.Kind != CustomLoopRunEventKind.NodeAttemptCompleted
            || evidence.Kind != CustomLoopSequentialNodeEvidenceKind.CompletedOutcome
            || evidence.Disposition != CustomLoopSequentialNodeDisposition.Completed
            || !IsHumanInputNodeEvent(run, item)
            || run.Frontier?.Payload.Nodes.ElementAtOrDefault(evidence.ActivationOrdinal) is not
            {
                Descriptor.Kind: GovernedLoopNodeKind.HumanInput,
                Status: GovernedLoopNodeExecutionStatus.Completed,
                OutcomeEvidenceId: not null,
                OutcomeEvidenceHash: not null,
            } activation
            || !string.Equals(activation.OutcomeEvidenceId, item.EventId, StringComparison.Ordinal)
            || !string.Equals(activation.OutcomeEvidenceHash, evidence.OutcomeArtifactHash, StringComparison.Ordinal))
        {
            return false;
        }

        return run.HumanInputWaitingCheckpoints.Count(checkpoint => checkpoint is not null
            && checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Terminal
            && checkpoint.Binding.ActivationOrdinal == evidence.ActivationOrdinal
            && checkpoint.Binding.NodeVisitOrdinal == evidence.VisitOrdinal
            && string.Equals(checkpoint.Binding.NodeId, evidence.NodeId, StringComparison.Ordinal)
            && string.Equals(checkpoint.Binding.CycleId, evidence.CycleId, StringComparison.Ordinal)
            && checkpoint.Binding.CycleIteration == evidence.CycleIteration
            && checkpoint.Evidence is [
            { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published },
            { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered },
            { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized },
            ]) == 1;
    }

    private static bool HasExactNoResponseHumanInputTerminalWithoutDispatch(
        CustomLoopRunRecord run,
        CustomLoopRunEvent item,
        CustomLoopSequentialNodeEvidence evidence)
    {
        if (item.Kind != CustomLoopRunEventKind.NodeAttemptFailed
            || !IsHumanInputNodeEvent(run, item)
            || item.FailureEvidence is not
            {
                CausalEvidence: [{ EvidenceId: var lifecycleOperationId, EvidenceHash: var requestHash }],
            } failure
            || evidence.FailureEvidenceId is null
            || evidence.FailureEvidenceHash is null
            || !string.Equals(evidence.FailureEvidenceId, failure.EvidenceId, StringComparison.Ordinal)
            || !string.Equals(evidence.FailureEvidenceHash, failure.ContentHash, StringComparison.Ordinal)
            || run.Frontier?.Payload.Nodes.ElementAtOrDefault(evidence.ActivationOrdinal) is not
            {
                Descriptor.Kind: GovernedLoopNodeKind.HumanInput,
                OutcomeEvidenceId: not null,
                OutcomeEvidenceHash: not null,
            } activation
            || !string.Equals(activation.OutcomeEvidenceId, item.EventId, StringComparison.Ordinal)
            || !string.Equals(activation.OutcomeEvidenceHash, evidence.OutcomeArtifactHash, StringComparison.Ordinal))
        {
            return false;
        }

        var checkpoints = run.HumanInputWaitingCheckpoints.Where(checkpoint => checkpoint is not null
            && checkpoint.Binding.ActivationOrdinal == evidence.ActivationOrdinal
            && checkpoint.Binding.NodeVisitOrdinal == evidence.VisitOrdinal
            && string.Equals(checkpoint.Binding.NodeId, evidence.NodeId, StringComparison.Ordinal)
            && string.Equals(checkpoint.Binding.CycleId, evidence.CycleId, StringComparison.Ordinal)
            && checkpoint.Binding.CycleIteration == evidence.CycleIteration).Take(2).ToArray();
        if (checkpoints.Length != 1 || !string.Equals(checkpoints[0].Request.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return false;
        }

        var checkpoint = checkpoints[0];
        GovernedLoopHumanInputWaitingCheckpointEvidenceKind expectedCheckpointEvidenceKind;
        GovernedLoopNodeExecutionStatus expectedActivationStatus;
        CustomLoopSequentialNodeEvidenceKind expectedEvidenceKind;
        CustomLoopSequentialNodeDisposition expectedDisposition;
        string expectedFailureCode;
        switch (checkpoint.Posture)
        {
            case GovernedLoopHumanInputWaitingCheckpointPosture.Expired:
                expectedCheckpointEvidenceKind = GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired;
                expectedActivationStatus = GovernedLoopNodeExecutionStatus.Failed;
                expectedEvidenceKind = CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection;
                expectedDisposition = CustomLoopSequentialNodeDisposition.Rejected;
                expectedFailureCode = "human-input-expired";
                break;
            case GovernedLoopHumanInputWaitingCheckpointPosture.Rejected:
                expectedCheckpointEvidenceKind = GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected;
                expectedActivationStatus = GovernedLoopNodeExecutionStatus.Failed;
                expectedEvidenceKind = CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection;
                expectedDisposition = CustomLoopSequentialNodeDisposition.Rejected;
                expectedFailureCode = "human-input-rejected";
                break;
            case GovernedLoopHumanInputWaitingCheckpointPosture.NeedsReview:
                expectedCheckpointEvidenceKind = GovernedLoopHumanInputWaitingCheckpointEvidenceKind.NeedsReview;
                expectedActivationStatus = GovernedLoopNodeExecutionStatus.ReviewBlocked;
                expectedEvidenceKind = CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention;
                expectedDisposition = CustomLoopSequentialNodeDisposition.NeedsReview;
                expectedFailureCode = "human-input-supersession-unresolved";
                break;
            default:
                return false;
        }

        if (activation.Status != expectedActivationStatus
            || evidence.Kind != expectedEvidenceKind
            || evidence.Disposition != expectedDisposition
            || !string.Equals(failure.ServerCode, expectedFailureCode, StringComparison.Ordinal)
            || !string.Equals(item.EventId, HumanInputNoResponseEventId(expectedFailureCode, lifecycleOperationId), StringComparison.Ordinal)
            || checkpoint.Evidence is not [
            { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published },
            { Kind: var evidenceKind },
            ]
            || evidenceKind != expectedCheckpointEvidenceKind)
        {
            return false;
        }

        return checkpoint.Evidence[1].OccurredAtUtc == item.TimestampUtc
            && failure.ObservedAtUtc == item.TimestampUtc;
    }

    private static string HumanInputNoResponseEventId(string failureCode, string lifecycleOperationId)
    {
        var operationHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(lifecycleOperationId)));
        return "human-input-" + failureCode + "-" + operationHash[..24];
    }

    private static bool HasExactRetryExhaustionWithoutDispatch(
        IReadOnlyList<CustomLoopRunEvent> events,
        int eventIndex,
        CustomLoopRunEvent item,
        CustomLoopSequentialNodeEvidence evidence)
    {
        if (evidence.Kind != CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection
            || evidence.Disposition != CustomLoopSequentialNodeDisposition.Rejected
            || evidence.Attempt is not { } attempt
            || item.FailureEvidence is not
            {
                FailureClass: GovernedLoopFailureClass.Exhaustion,
                EffectCertainty: GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted,
                RetrySafety: GovernedLoopFailureRetrySafety.NotRetryable,
            } failure
            || failure.CausalEvidence.Count != 1)
        {
            return false;
        }

        var terminals = events.Take(eventIndex)
            .Where(candidate => candidate?.RetryState is
            {
                Disposition: GovernedLoopRetryStateDisposition.Exhausted,
            } state
                && state.Identity.ActivationOrdinal == evidence.ActivationOrdinal
                && state.Identity.VisitOrdinal == evidence.VisitOrdinal
                && string.Equals(state.Identity.NodeId, evidence.NodeId, StringComparison.Ordinal)
                && string.Equals(candidate.EventId, failure.CausalEvidence[0].EvidenceId, StringComparison.Ordinal)
                && string.Equals(state.ContentHash, failure.CausalEvidence[0].EvidenceHash, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (terminals.Length != 1 || terminals[0].RetryState is not { } terminal)
        {
            return false;
        }

        var predecessors = events.TakeWhile(candidate => !ReferenceEquals(candidate, terminals[0]))
            .Select(candidate => candidate?.RetryState)
            .Where(candidate => candidate is not null
                && string.Equals(candidate.Identity.SeriesId, terminal.Identity.SeriesId, StringComparison.Ordinal)
                && candidate.StateVersion == terminal.StateVersion - 1)
            .Take(2)
            .ToArray();
        return predecessors.Length == 1
            && predecessors[0] is
            {
                Disposition: GovernedLoopRetryStateDisposition.Due,
                NextAttempt: var nextAttempt,
            }
            && nextAttempt == attempt;
    }

    private static bool HasValidSequentialRouteShape(CustomLoopSequentialNodeEvidence evidence)
    {
        var selected = evidence.SelectedControlEdgeIds;
        var skipped = evidence.SkippedControlEdgeIds;
        if (selected is null
            || skipped is null
            || selected.Count > EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxOutgoingEdges
            || skipped.Count > EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxOutgoingEdges
            || !IsSortedUniqueIdentifiers(selected)
            || !IsSortedUniqueIdentifiers(skipped)
            || selected.Intersect(skipped, StringComparer.Ordinal).Any())
        {
            return false;
        }

        if (evidence.Kind == CustomLoopSequentialNodeEvidenceKind.TopologySkipped)
        {
            return evidence.ControlOutcome is null
                && selected.Count == 0
                && skipped.Count == 0
                && evidence.GoverningActivationOrdinal is >= 0
                && evidence.GoverningActivationOrdinal < evidence.ActivationOrdinal
                && CustomLoopArtifactIdentifier.IsValid(evidence.GoverningControlEdgeId);
        }

        if (evidence.GoverningActivationOrdinal is not null || evidence.GoverningControlEdgeId is not null)
        {
            return false;
        }

        if (evidence.Kind is CustomLoopSequentialNodeEvidenceKind.DispatchStarted or CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention)
        {
            return evidence.ControlOutcome is null && selected.Count == 0 && skipped.Count == 0;
        }

        return evidence.ControlOutcome is { } outcome
            && outcome != GovernedLoopControlCondition.Unknown
            && Enum.IsDefined(outcome);
    }

    private static void ValidateFailureEvidence(CustomLoopRunEvent item, string field, CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        var sequential = item.SequentialNodeEvidence;
        if (item.FailureEvidence is not { } failure)
        {
            if (sequential?.FailureEvidenceId is not null
                || sequential?.FailureEvidenceHash is not null
                || sequential?.Kind is CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection or CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention)
            {
                Add(errors, "failure_evidence_required", $"{field}.failureEvidence", "A failed or review-blocked activation requires one exact immutable classified failure artifact.");
            }
            return;
        }

        if (!GovernedLoopFailureEvidenceContract.IsValid(failure)
            || sequential is null
            || sequential.Kind is not (CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection or CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention)
            || sequential.Kind switch
            {
                CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection => item.Kind != CustomLoopRunEventKind.NodeAttemptFailed,
                CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention => item.Kind is not (CustomLoopRunEventKind.NodeAttemptFailed or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.ExitDecisionCompleted),
                _ => true,
            }
            || !string.Equals(sequential.FailureEvidenceId, failure.EvidenceId, StringComparison.Ordinal)
            || !string.Equals(sequential.FailureEvidenceHash, failure.ContentHash, StringComparison.Ordinal)
            || !string.Equals(failure.WorkspaceId, run.SequentialAdapterBinding?.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(failure.RunId, run.Id, StringComparison.Ordinal)
            || !Equals(failure.Revision, sequential.Revision)
            || failure.ExecutionGeneration != sequential.ExecutionGeneration
            || failure.ActivationOrdinal != sequential.ActivationOrdinal
            || failure.VisitOrdinal != sequential.VisitOrdinal
            || !string.Equals(failure.NodeId, sequential.NodeId, StringComparison.Ordinal)
            || failure.Attempt != sequential.Attempt)
        {
            Add(errors, "invalid_failure_evidence", $"{field}.failureEvidence", "Classified failure evidence must authenticate and match the exact failed run-node-attempt coordinates and sequential evidence reference.");
            return;
        }

        var requiresReview = GovernedLoopFailureEvidenceContract.RequiresReview(failure);
        if (requiresReview != (sequential.Kind == CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention))
        {
            Add(errors, "failure_review_posture_mismatch", $"{field}.failureEvidence", "Only ambiguity or integrity failure evidence may produce a review-blocked sequential outcome.");
        }
    }

    private static void ValidateRetryState(
        CustomLoopRunEvent item,
        int eventIndex,
        string field,
        CustomLoopRunRecord run,
        Dictionary<(int ActivationOrdinal, int VisitOrdinal), string> seriesByActivation,
        Dictionary<string, GovernedLoopRetryState> latestBySeries,
        List<CustomLoopValidationError> errors)
    {
        if (item.RetryState is not { } state)
        {
            if (item.Kind == CustomLoopRunEventKind.RetryStateChanged)
            {
                Add(errors, "retry_state_required", $"{field}.retryState", "A retry-state event requires one exact authenticated retry state.");
            }
            return;
        }

        var identity = state.Identity;
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(identity.ActivationOrdinal);
        var binding = run.SequentialAdapterBinding;
        var failures = run.Events.Take(eventIndex)
            .Select(candidate => candidate?.FailureEvidence)
            .Where(candidate => candidate is not null
                && string.Equals(candidate.EvidenceId, state.FailureEvidenceId, StringComparison.Ordinal)
                && string.Equals(candidate.ContentHash, state.FailureEvidenceHash, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var failure = failures.Length == 1 ? failures[0] : null;
        var attemptStarts = run.Events.Take(eventIndex)
            .Where(candidate => candidate?.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            } start
                && string.Equals(candidate.EventId, state.CurrentAttemptOperationId, StringComparison.Ordinal)
                && start.ActivationOrdinal == identity.ActivationOrdinal
                && start.VisitOrdinal == identity.VisitOrdinal
                && string.Equals(start.NodeId, identity.NodeId, StringComparison.Ordinal)
                && start.Attempt == state.CurrentAttempt)
            .Take(2)
            .ToArray();
        if (item.Kind != CustomLoopRunEventKind.RetryStateChanged
            || !GovernedLoopRetryContract.IsValid(state)
            || binding is null
            || !string.Equals(identity.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(identity.RunId, run.Id, StringComparison.Ordinal)
            || identity.Revision != binding.ExecutionBinding.Revision
            || identity.ExecutionGeneration != binding.ExecutionBinding.ExecutionGeneration
            || activation is null
            || activation.ActivationOrdinal != identity.ActivationOrdinal
            || activation.VisitOrdinal != identity.VisitOrdinal
            || !string.Equals(activation.NodeId, identity.NodeId, StringComparison.Ordinal)
            || failure is null
            || failure.ActivationOrdinal != identity.ActivationOrdinal
            || failure.VisitOrdinal != identity.VisitOrdinal
            || !string.Equals(failure.NodeId, identity.NodeId, StringComparison.Ordinal)
            || failure.Attempt != state.CurrentAttempt
            || attemptStarts.Length != 1
            || item.Attempt != state.CurrentAttempt
            || !string.Equals(item.StepId, identity.NodeId, StringComparison.Ordinal)
            || item.SequentialNodeEvidence is not null
            || item.FailureEvidence is not null
            || item.ContextBlocks is not { Length: 0 }
            || item.CanonicalOutput is not null
            || item.ToolAuthority is not null
            || item.ToolEvidence is not null
            || item.PureNodeOutcomeJson is not null
            || item.WaitContinuationEvidenceHash is not null
            || item.ModelExecutionEvidence is not null)
        {
            Add(errors, "invalid_retry_state", $"{field}.retryState", "Retry state must authenticate the exact admitted run, revision, activation, retained failure, and value-free event coordinates.");
            return;
        }

        var activationKey = (identity.ActivationOrdinal, identity.VisitOrdinal);
        if (seriesByActivation.TryGetValue(activationKey, out var priorSeriesId)
            && !string.Equals(priorSeriesId, identity.SeriesId, StringComparison.Ordinal))
        {
            Add(errors, "retry_series_substituted", $"{field}.retryState.identity.seriesId", "One activation visit may retain only one immutable retry series.");
            return;
        }
        seriesByActivation[activationKey] = identity.SeriesId;

        if (!latestBySeries.TryGetValue(identity.SeriesId, out var prior))
        {
            if (state.StateVersion != 1 || state.Disposition != GovernedLoopRetryStateDisposition.FailureRetained)
            {
                Add(errors, "retry_series_origin_required", $"{field}.retryState.stateVersion", "A retry series must begin with state version one retaining the exact first failure.");
                return;
            }
        }
        else if (!GovernedLoopRetryContract.IsValidTransition(prior, state))
        {
            Add(errors, "invalid_retry_state_transition", $"{field}.retryState.stateVersion", "Retry-state versions must form one contiguous authenticated monotonic transition chain.");
            return;
        }

        latestBySeries[identity.SeriesId] = state;
    }

    private static bool IsSortedUniqueIdentifiers(IReadOnlyList<string> values)
        => values.All(value => CustomLoopArtifactIdentifier.IsValid(value))
            && values.SequenceEqual(values.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal), StringComparer.Ordinal);

    private static void RegisterSequentialVisit(
        CustomLoopSequentialNodeEvidence evidence,
        string field,
        Dictionary<string, int> latestVisits,
        List<CustomLoopValidationError> errors)
    {
        var expectedVisit = latestVisits.GetValueOrDefault(evidence.NodeId) + 1;
        if (evidence.VisitOrdinal != expectedVisit)
        {
            Add(errors, "non_monotonic_sequential_node_visit", $"{field}.sequentialNodeEvidence.visitOrdinal", "Sequential node visits must be unique and increase contiguously for each canonical node identity.");
            return;
        }

        latestVisits[evidence.NodeId] = evidence.VisitOrdinal;
    }

    private static void ValidateSequentialEvidenceFrontierCoordinates(
        CustomLoopRunEvent item,
        CustomLoopSequentialNodeEvidence evidence,
        string field,
        CustomLoopRunRecord run,
        List<CustomLoopValidationError> errors)
    {
        if (run.Frontier?.Payload.Nodes.ElementAtOrDefault(evidence.ActivationOrdinal) is not { } activation
            || activation.ActivationOrdinal != evidence.ActivationOrdinal
            || activation.VisitOrdinal != evidence.VisitOrdinal
            || !string.Equals(activation.NodeId, evidence.NodeId, StringComparison.Ordinal)
            || !string.Equals(activation.CycleId, evidence.CycleId, StringComparison.Ordinal)
            || activation.CycleIteration != evidence.CycleIteration
            || !HasCompatibleFrontierAttempt(run.Events, activation, evidence)
            || evidence.Kind == CustomLoopSequentialNodeEvidenceKind.TopologySkipped && activation.Status != GovernedLoopNodeExecutionStatus.Skipped
            || evidence.Kind == CustomLoopSequentialNodeEvidenceKind.TopologySkipped
                && !HasExactGoverningSkipActivation(run.Frontier.Payload.Nodes, activation, evidence)
            || evidence.ControlOutcome is not null
                && !evidence.SelectedControlEdgeIds.Concat(evidence.SkippedControlEdgeIds).Order(StringComparer.Ordinal)
                    .SequenceEqual(activation.OutgoingControlEdgeIds, StringComparer.Ordinal)
            || evidence.ControlOutcome is { } controlOutcome
                && !IsHistoricalRetryAttempt(run.Events, activation, evidence)
                && (activation.Status is not (GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running)
                && (activation.ControlOutcome != controlOutcome
                    || !activation.SelectedControlEdgeIds.SequenceEqual(evidence.SelectedControlEdgeIds, StringComparer.Ordinal)
                    || !activation.SkippedControlEdgeIds.SequenceEqual(evidence.SkippedControlEdgeIds, StringComparer.Ordinal)))
            || activation.Status == GovernedLoopNodeExecutionStatus.Skipped
                && (!string.Equals(activation.OutcomeEvidenceId, item.EventId, StringComparison.Ordinal)
                    || !string.Equals(activation.OutcomeEvidenceHash, evidence.OutcomeArtifactHash, StringComparison.Ordinal)))
        {
            Add(errors, "sequential_node_activation_mismatch", $"{field}.sequentialNodeEvidence.activationOrdinal", "Sequential evidence must identify the exact durable frontier activation, visit, cycle, attempt, and committed route coordinates.");
        }
    }

    private static bool HasCompatibleFrontierAttempt(
        IReadOnlyList<CustomLoopRunEvent> events,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopSequentialNodeEvidence evidence)
    {
        if (activation.Attempt == evidence.Attempt)
        {
            return true;
        }

        return IsHistoricalRetryAttempt(events, activation, evidence);
    }

    private static bool IsHistoricalRetryAttempt(
        IReadOnlyList<CustomLoopRunEvent> events,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopSequentialNodeEvidence evidence)
        => evidence.Attempt is { } historicalAttempt
            && activation.Attempt is { } currentAttempt
            && historicalAttempt < currentAttempt
            && events.Any(item => item?.RetryState is { } state
                && state.Identity.ActivationOrdinal == evidence.ActivationOrdinal
                && state.Identity.VisitOrdinal == evidence.VisitOrdinal
                && state.CurrentAttempt == historicalAttempt
                && string.Equals(state.Identity.NodeId, evidence.NodeId, StringComparison.Ordinal));

    private static bool HasPriorRetryDispatch(
        IReadOnlyList<CustomLoopRunEvent> events,
        int eventIndex,
        CustomLoopSequentialNodeEvidence evidence)
        => events.Take(eventIndex).Any(item => item?.RetryState is
        {
            Disposition: GovernedLoopRetryStateDisposition.Dispatched,
            NextAttempt: { } nextAttempt,
        } state
            && nextAttempt == evidence.Attempt
            && state.Identity.ActivationOrdinal == evidence.ActivationOrdinal
            && state.Identity.VisitOrdinal == evidence.VisitOrdinal
            && string.Equals(state.Identity.NodeId, evidence.NodeId, StringComparison.Ordinal));

    private static bool HasExactGoverningSkipActivation(
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> activations,
        GovernedLoopNodeExecutionEvidence skipped,
        CustomLoopSequentialNodeEvidence evidence)
    {
        if (evidence.GoverningActivationOrdinal is not { } governingOrdinal
            || evidence.GoverningControlEdgeId is not { } governingEdgeId
            || activations.ElementAtOrDefault(governingOrdinal) is not { } governing)
        {
            return false;
        }

        return governing.ActivationOrdinal == governingOrdinal
            && governing.Status is GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed
            && governing.SkippedControlEdgeIds.Contains(governingEdgeId, StringComparer.Ordinal)
            && governing.OutgoingControlEdgeIds.Contains(governingEdgeId, StringComparer.Ordinal)
            && skipped.IncomingControlEdgeIds.Contains(governingEdgeId, StringComparer.Ordinal);
    }

    private static void ValidateSequentialNodeCoordinates(
        CustomLoopRunEvent item,
        int eventIndex,
        CustomLoopSequentialNodeEvidence evidence,
        string field,
        CustomLoopRunRecord run,
        List<CustomLoopValidationError> errors)
    {
        if (item.Kind == CustomLoopRunEventKind.TopologyNodeSkipped)
        {
            if (!string.Equals(item.StepId, evidence.NodeId, StringComparison.Ordinal)
                || item.Attempt is not null
                || item.Iteration != evidence.CycleIteration)
            {
                Add(errors, "sequential_skip_coordinates_mismatch", field, "A topology-pruning event must identify the exact skipped activation node and cycle iteration without dispatch-attempt coordinates.");
            }

            return;
        }

        var isExitFailure = item.Kind == CustomLoopRunEventKind.NodeAttemptFailed
            && HasPriorSequentialDispatch(run.Events, eventIndex, evidence, CustomLoopRunEventKind.ExitDecisionStarted);
        if (isExitFailure)
        {
            if (!string.Equals(item.StepId, "exit", StringComparison.Ordinal))
            {
                Add(errors, "sequential_exit_step_mismatch", $"{field}.stepId", "Sequential Exit-decision evidence requires the reserved legacy adapter step id 'exit'.");
            }
        }
        else if (item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.NodeAttemptFailed)
        {
            var isPureNode = IsPureNodeEvent(run, item);
            var isTopologyNode = IsTopologyNodeEvent(run, item);
            var isWaitNode = IsWaitNodeEvent(run, item);
            var isHumanInputNode = IsHumanInputNodeEvent(run, item);
            var isRecoverableAction = IsRecoverableActionEvent(run, item);
            var isFailNode = IsFailNodeEvent(run, item);
            var hasValidPureEventShape = item.Kind switch
            {
                CustomLoopRunEventKind.NodeAttemptStarted => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
                CustomLoopRunEventKind.NodeAttemptCompleted => item.PureNodeOutcomeJson is not null,
                CustomLoopRunEventKind.NodeAttemptFailed => HasPriorPureDispatch(run.Events, eventIndex, evidence),
                _ => false,
            };
            if (isPureNode)
            {
                if (!string.Equals(evidence.NodeId, item.StepId, StringComparison.Ordinal) || !hasValidPureEventShape)
                {
                    Add(errors, "sequential_pure_node_step_mismatch", $"{field}.sequentialNodeEvidence.nodeId", "Sequential Transform or Validate evidence must identify its exact pinned frontier node and use the pure-node start, completion, or failure envelope.");
                }

                return;
            }

            if (isTopologyNode)
            {
                var hasValidTopologyShape = item.Kind switch
                {
                    CustomLoopRunEventKind.NodeAttemptStarted => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
                    CustomLoopRunEventKind.NodeAttemptCompleted => item.PureNodeOutcomeJson is null,
                    CustomLoopRunEventKind.NodeAttemptFailed => HasPriorSequentialDispatch(run.Events, eventIndex, evidence, CustomLoopRunEventKind.NodeAttemptStarted),
                    _ => false,
                };
                if (!string.Equals(evidence.NodeId, item.StepId, StringComparison.Ordinal)
                    || item.Iteration != (evidence.CycleIteration ?? 1)
                    || !hasValidTopologyShape)
                {
                    Add(errors, "sequential_topology_node_step_mismatch", $"{field}.sequentialNodeEvidence.nodeId", "Sequential Condition or Join evidence must identify its exact activation, cycle iteration, and deterministic start, completion, or failure envelope.");
                }

                return;
            }

            if (isWaitNode)
            {
                var hasValidWaitShape = item.Kind switch
                {
                    CustomLoopRunEventKind.NodeAttemptStarted => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes
                        && item.WaitContinuationEvidenceHash is null,
                    CustomLoopRunEventKind.NodeAttemptCompleted => item.PureNodeOutcomeJson is null
                        && item.WaitContinuationEvidenceHash is not null,
                    CustomLoopRunEventKind.NodeAttemptFailed => HasPriorSequentialDispatch(run.Events, eventIndex, evidence, CustomLoopRunEventKind.NodeAttemptStarted)
                        && item.WaitContinuationEvidenceHash is null,
                    _ => false,
                };
                if (!string.Equals(evidence.NodeId, item.StepId, StringComparison.Ordinal)
                    || item.Iteration != (evidence.CycleIteration ?? 1)
                    || !hasValidWaitShape)
                {
                    Add(errors, "sequential_wait_node_step_mismatch", $"{field}.sequentialNodeEvidence.nodeId", "Sequential Wait evidence must identify its exact activation and use the canonical start, completion, or failure envelope.");
                }

                return;
            }

            if (isHumanInputNode)
            {
                var hasValidHumanInputShape = item.Kind switch
                {
                    CustomLoopRunEventKind.NodeAttemptStarted => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes,
                    CustomLoopRunEventKind.NodeAttemptCompleted => item.PureNodeOutcomeJson is null
                        && item.WaitContinuationEvidenceHash is null
                        && item.CanonicalOutput is null,
                    CustomLoopRunEventKind.NodeAttemptFailed => item.FailureEvidence is not null,
                    _ => false,
                };
                if (!string.Equals(evidence.NodeId, item.StepId, StringComparison.Ordinal)
                    || item.Iteration != (evidence.CycleIteration ?? 1)
                    || !hasValidHumanInputShape)
                {
                    Add(errors, "sequential_human_input_node_step_mismatch", $"{field}.sequentialNodeEvidence.nodeId", "Sequential Human Input evidence must identify the exact activation and retain the value-free accepted-response or classified no-response outcome envelope.");
                }

                return;
            }

            if (isRecoverableAction)
            {
                var hasValidActionShape = item.Kind switch
                {
                    CustomLoopRunEventKind.NodeAttemptStarted => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
                    CustomLoopRunEventKind.NodeAttemptCompleted => item.CanonicalOutput is not null && IsRecoverableActionResult(run, item, item.CanonicalOutput),
                    CustomLoopRunEventKind.NodeAttemptFailed => HasPriorSequentialDispatch(run.Events, eventIndex, evidence, CustomLoopRunEventKind.NodeAttemptStarted),
                    _ => false,
                };
                if (!string.Equals(evidence.NodeId, item.StepId, StringComparison.Ordinal)
                    || item.Iteration != (evidence.CycleIteration ?? 1)
                    || !hasValidActionShape)
                {
                    Add(errors, "sequential_recoverable_action_step_mismatch", $"{field}.sequentialNodeEvidence.nodeId", "Sequential recoverable Action evidence must identify its exact activation and use the canonical reserved start, value-free completion, or failure envelope.");
                }

                return;
            }

            if (isFailNode)
            {
                var hasValidFailShape = item.Kind switch
                {
                    CustomLoopRunEventKind.NodeAttemptStarted => item.TraceReservationUtf8Bytes == CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
                    CustomLoopRunEventKind.NodeAttemptFailed => HasPriorSequentialDispatch(run.Events, eventIndex, evidence, CustomLoopRunEventKind.NodeAttemptStarted)
                        && item.FailureEvidence is not null,
                    _ => false,
                };
                if (!string.Equals(evidence.NodeId, item.StepId, StringComparison.Ordinal)
                    || item.Iteration != (evidence.CycleIteration ?? 1)
                    || !hasValidFailShape)
                {
                    Add(errors, "sequential_fail_node_step_mismatch", $"{field}.sequentialNodeEvidence.nodeId", "Sequential Fail evidence must identify its exact activation and use the canonical reserved start and classified failure envelope.");
                }

                return;
            }

            var isAdmittedInferenceStep = run.AdmittedDefinition?.InferenceSteps?.Any(step => string.Equals(step.Id, item.StepId, StringComparison.Ordinal)) == true;
            if (string.Equals(item.StepId, "exit", StringComparison.Ordinal)
                || !string.Equals(evidence.NodeId, item.StepId, StringComparison.Ordinal)
                || !isAdmittedInferenceStep)
            {
                Add(errors, "sequential_inference_step_mismatch", $"{field}.sequentialNodeEvidence.nodeId", "Sequential inference evidence must identify the containing event's exact admitted legacy inference-step id.");
            }
        }
        else if (item.Kind is CustomLoopRunEventKind.ExitDecisionStarted or CustomLoopRunEventKind.ExitDecisionCompleted)
        {
            if (!string.Equals(item.StepId, "exit", StringComparison.Ordinal))
            {
                Add(errors, "sequential_exit_step_mismatch", $"{field}.stepId", "Sequential Exit-decision evidence requires the reserved legacy adapter step id 'exit'.");
            }
        }
        else if (item.Kind == CustomLoopRunEventKind.Admitted && (item.Iteration is not null || item.StepId is not null || item.Attempt is not null))
        {
            Add(errors, "sequential_trigger_coordinates_mismatch", field, "The admitted Manual Trigger outcome cannot carry legacy iteration, step, or attempt coordinates.");
        }
    }

    private static bool HasPriorSequentialDispatch(
        IReadOnlyList<CustomLoopRunEvent> events,
        int terminalIndex,
        CustomLoopSequentialNodeEvidence evidence,
        CustomLoopRunEventKind expectedStartKind)
    {
        return terminalIndex > 0 && events.Take(terminalIndex).Any(candidate => candidate is { Kind: var kind }
            && kind == expectedStartKind
            && candidate.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted } start
            && start.ActivationOrdinal == evidence.ActivationOrdinal
            && start.VisitOrdinal == evidence.VisitOrdinal
            && string.Equals(start.NodeId, evidence.NodeId, StringComparison.Ordinal)
            && start.Attempt == evidence.Attempt
            && string.Equals(start.CycleId, evidence.CycleId, StringComparison.Ordinal)
            && start.CycleIteration == evidence.CycleIteration);
    }

    private static bool HasPriorPureDispatch(
        IReadOnlyList<CustomLoopRunEvent> events,
        int terminalIndex,
        CustomLoopSequentialNodeEvidence evidence)
    {
        return terminalIndex > 0 && events.Take(terminalIndex).Any(candidate => candidate is
        {
            Kind: CustomLoopRunEventKind.NodeAttemptStarted,
            TraceReservationUtf8Bytes: CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
            SequentialNodeEvidence: { Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted } start,
        }
            && start.ActivationOrdinal == evidence.ActivationOrdinal
            && start.VisitOrdinal == evidence.VisitOrdinal
            && string.Equals(start.NodeId, evidence.NodeId, StringComparison.Ordinal)
            && start.Attempt == evidence.Attempt
            && string.Equals(start.CycleId, evidence.CycleId, StringComparison.Ordinal)
            && start.CycleIteration == evidence.CycleIteration);
    }

    private static void ValidateIntegrityReservationScope(IReadOnlyList<CustomLoopRunEvent> events, List<CustomLoopValidationError> errors)
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index]?.ToolEvidence is not { Phase: CustomLoopToolEvidencePhase.IntegrityFailed } integrity)
            {
                continue;
            }

            var hasEarlierReservation = events.Take(index).Any(item => item?.ToolEvidence is { Phase: CustomLoopToolEvidencePhase.RequestReserved } reservation
                && reservation.RequestOrdinal == integrity.RequestOrdinal
                && string.Equals(reservation.RequestCorrelationId, integrity.RequestCorrelationId, StringComparison.Ordinal));
            var expected = hasEarlierReservation
                ? CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes
                : CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes;
            if (integrity.ReservedUtf8Bytes != expected)
            {
                Add(
                    errors,
                    "invalid_tool_integrity_reservation",
                    $"events[{index}].toolEvidence.reservedUtf8Bytes",
                    hasEarlierReservation
                        ? "A compatibility integrity marker attached to an earlier reservation must retain the original full reservation class."
                        : "A standalone non-actuating integrity marker must use the bounded repeated-request reservation class.");
            }
        }
    }

    private static void ValidateTraceReservation(CustomLoopRunEvent item, string field, CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        var startsAttempt = item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted;
        var expectedReservation = IsPureNodeEvent(run, item) || IsTopologyNodeEvent(run, item) || IsRecoverableActionEvent(run, item) || IsFailNodeEvent(run, item)
            ? CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes
            : CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes;
        if (startsAttempt && item.TraceReservationUtf8Bytes != expectedReservation)
        {
            Add(errors, "attempt_trace_reservation_required", $"{field}.traceReservationUtf8Bytes", "Every node-attempt start must atomically reserve its exact bounded mandatory outcome footprint before dispatch.");
        }
        else if (!startsAttempt && item.TraceReservationUtf8Bytes is not null)
        {
            Add(errors, "unexpected_trace_reservation", $"{field}.traceReservationUtf8Bytes", "Only node-attempt start events may carry an attempt trace reservation.");
        }
    }

    private static bool IsPureNodeEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        var node = run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal);
        return node?.Descriptor.Kind is GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate;
    }

    private static bool IsTopologyNodeEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        var node = run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal);
        return node?.Descriptor.Kind is GovernedLoopNodeKind.Condition or GovernedLoopNodeKind.Join;
    }

    private static bool IsWaitNodeEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        return run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal)?.Descriptor.Kind == GovernedLoopNodeKind.Wait;
    }

    private static bool IsHumanInputNodeEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        return run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal)?.Descriptor.Kind == GovernedLoopNodeKind.HumanInput;
    }

    private static bool IsRecoverableActionEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        var descriptor = run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal)?.Descriptor;
        return WorkspaceActionNodeDescriptors.TryResolve(descriptor, out _)
            || CommandActionNodeDescriptors.IsCommandAction(descriptor);
    }

    private static bool IsFailNodeEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        return run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal)?.Descriptor.Kind == GovernedLoopNodeKind.Fail;
    }

    private static bool IsRecoverableActionResult(CustomLoopRunRecord run, CustomLoopRunEvent item, string canonicalOutput)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        var descriptor = run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal)?.Descriptor;
        return WorkspaceActionNodeDescriptors.TryResolve(descriptor, out _)
            ? WorkspaceActionResultContract.TryParse(canonicalOutput, out _)
            : CommandActionNodeDescriptors.IsCommandAction(descriptor)
                && CommandActionResultContract.TryParse(canonicalOutput, out _);
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

        var isNodeEvent = item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.NodeAttemptFailed or CustomLoopRunEventKind.ToolRequestReserved or CustomLoopRunEventKind.ToolGovernanceDecided or CustomLoopRunEventKind.ToolOutcomeObserved or CustomLoopRunEventKind.ToolIntegrityFailed or CustomLoopRunEventKind.RetryStateChanged;
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

        if (evidence.RequestOrdinal < 1 || evidence.RequestOrdinal > CustomLoopLimits.MaxRecordedGovernedToolRequestsPerAttempt)
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

        ValidateText(evidence.TargetPath, $"{field}.targetPath", CustomLoopLimits.MaxGovernedToolTargetCharacters, required: true, errors, requireNormalized: false);
        ValidateOptionalText(evidence.Content, $"{field}.content", CustomLoopLimits.MaxGovernedToolArgumentCharacters, errors, requireNormalized: false);
        ValidateOptionalText(evidence.Pattern, $"{field}.pattern", CustomLoopLimits.MaxGovernedToolArgumentCharacters, errors, requireNormalized: false);
        ValidateOptionalText(evidence.ResolvedTarget, $"{field}.resolvedTarget", CustomLoopLimits.MaxGovernedToolTargetCharacters, errors, requireNormalized: false);
        var isStandaloneIntegrity = evidence.Phase == CustomLoopToolEvidencePhase.IntegrityFailed
            && evidence.ReservedUtf8Bytes == CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes;
        if (!isStandaloneIntegrity && evidence.ReservedUtf8Bytes != CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes)
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

        if (isStandaloneIntegrity
            && (evidence.BrokerRequestId is not null
                || evidence.Governance is not null
                || evidence.Outcome is not null
                || evidence.CanonicalResultReturnedToModel is not null
                || evidence.CanonicalResultHash is not null
                || evidence.CanonicalResultCharacterCount is not null
                || evidence.ReturnedToModel))
        {
            Add(errors, "invalid_tool_integrity_payload", field, "A non-actuating tool integrity record may contain only the exact request, authority, correlation, and reserved capacity.");
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

        if (checkpoint.ToolRequestsUsed < 0 || checkpoint.ToolRequestsUsed > CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun)
        {
            Add(errors, "tool_request_budget_out_of_range", "checkpoint.toolRequestsUsed", $"Persisted model-visible tool-request usage must be between 0 and {CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun}, including the one visible over-limit denial.");
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
        if (run.AdmittedDefinition?.InferenceSteps is { } steps
            && !steps.Any(step => string.Equals(step.Id, output.StepId, StringComparison.Ordinal))
            && !HasCompletedRetainedNodeOutcome(run, output.StepId))
        {
            Add(errors, "unknown_retained_step", $"{field}.stepId", "Retained output step id must exist in the admitted legacy definition or identify an exact completed retained-output node in the pinned sequential frontier.");
        }

        if (output.Iteration < 1 || output.Iteration > run.Checkpoint.Iteration)
        {
            Add(errors, "retained_output_iteration_out_of_range", $"{field}.iteration", "Retained output iteration must be within the executed checkpoint range.");
        }

        ValidateText(output.Content, $"{field}.content", CustomLoopLimits.MaxCanonicalModelOutputCharacters, required: false, errors, requireNormalized: false);
        ValidateContentHash(output.Content, output.ContentHash, $"{field}.contentHash", errors);
    }

    private static bool HasCompletedRetainedNodeOutcome(CustomLoopRunRecord run, string stepId)
    {
        if (run.SequentialAdapterBinding is null || run.Frontier is null)
        {
            return false;
        }

        return run.Events?.Any(item => item is
        {
            Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
            SequentialNodeEvidence:
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            } evidence,
        }
            && string.Equals(item.StepId, stepId, StringComparison.Ordinal)
            && string.Equals(evidence.NodeId, stepId, StringComparison.Ordinal)
            && run.Frontier.Payload.Nodes.ElementAtOrDefault(evidence.ActivationOrdinal) is { } activation
            && activation.ActivationOrdinal == evidence.ActivationOrdinal
            && activation.VisitOrdinal == evidence.VisitOrdinal
            && (activation.Descriptor.Kind is GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate
                && item.PureNodeOutcomeJson is not null
                || activation.Descriptor.Kind == GovernedLoopNodeKind.Action
                && item.CanonicalOutput is not null
                && (WorkspaceActionNodeDescriptors.TryResolve(activation.Descriptor, out _)
                    ? WorkspaceActionResultContract.TryParse(item.CanonicalOutput, out _)
                    : CommandActionNodeDescriptors.IsCommandAction(activation.Descriptor)
                        && CommandActionResultContract.TryParse(item.CanonicalOutput, out _)))
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item)) == true;
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

        if (!string.Equals(JsonSerializer.Serialize(current.CapabilityAdmission), JsonSerializer.Serialize(candidate.CapabilityAdmission), StringComparison.Ordinal))
        {
            Add(errors, "capability_admission_changed", "capabilityAdmission", "Historical capability pins and resolution evidence are immutable.");
        }


        if (!string.Equals(current.SequentialInvocationSnapshot?.ContentHash, candidate.SequentialInvocationSnapshot?.ContentHash, StringComparison.Ordinal)
            || !string.Equals(current.SequentialAdapterBinding?.ContentHash, candidate.SequentialAdapterBinding?.ContentHash, StringComparison.Ordinal))
        {
            Add(errors, "sequential_admission_changed", "sequentialAdapterBinding", "The immutable sequential invocation snapshot and adapter binding cannot change after run materialization.");
        }
    }

    private static void ValidateSequentialCheckpointAdvance(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (candidate.SequentialAdapterBinding is null || current.Checkpoint is null || candidate.Checkpoint is null)
        {
            return;
        }

        var advanced = candidate.Checkpoint.Iteration != current.Checkpoint.Iteration
            || candidate.Checkpoint.NextStepIndex != current.Checkpoint.NextStepIndex
            || candidate.Checkpoint.AcceptedRepeatCount != current.Checkpoint.AcceptedRepeatCount
            || candidate.Checkpoint.PendingExitDecision != current.Checkpoint.PendingExitDecision
            || candidate.Checkpoint.LastCommittedSequence != current.Checkpoint.LastCommittedSequence;
        if (!advanced)
        {
            return;
        }

        var hasPriorDurableOutcome = candidate.Events is not null
            && candidate.Events.Any(item => item is not null
                && item.Sequence > current.Checkpoint.LastCommittedSequence
                && item.Sequence <= candidate.Checkpoint.LastCommittedSequence
                && item.SequentialNodeEvidence is { Kind: not CustomLoopSequentialNodeEvidenceKind.DispatchStarted });
        if (!hasPriorDurableOutcome)
        {
            Add(errors, "sequential_outcome_required_before_checkpoint", "checkpoint", "A canonical sequential checkpoint cannot advance until exact terminal node evidence is already present earlier in the durable event stream.");
        }
    }

    private static string GetRequirementsHash(CustomLoopDefinition definition)
    {
        return CapabilityDependencyManifestHash.TryCompute(definition.CapabilityRequirements, out var hash, out _) ? hash!.Value : string.Empty;
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

    private static void ValidateAppendOnlyWaitEvidence(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.WaitEvidence is null || candidate.WaitEvidence is null || candidate.WaitEvidence.Count < current.WaitEvidence.Count)
        {
            Add(errors, "wait_evidence_history_truncated", "waitEvidence", "Persisted Wait activation evidence is append-only.");
            return;
        }

        var changed = 0;
        for (var index = 0; index < current.WaitEvidence.Count; index++)
        {
            var currentItem = current.WaitEvidence[index];
            var candidateItem = candidate.WaitEvidence[index];
            if (!HasSameWaitIdentity(currentItem, candidateItem))
            {
                Add(errors, "wait_evidence_history_changed", $"waitEvidence[{index}]", "Retained Wait coordinates and evidence phases are immutable; only missing checkpoint or continuation evidence may be appended.");
                continue;
            }

            if (string.Equals(currentItem.ContentHash, candidateItem.ContentHash, StringComparison.Ordinal))
            {
                continue;
            }

            changed++;
            var currentActivation = current.Frontier?.Payload.Nodes.ElementAtOrDefault(currentItem.ActivationOrdinal);
            var candidateActivation = candidate.Frontier?.Payload.Nodes.ElementAtOrDefault(candidateItem.ActivationOrdinal);
            var attachedPark = currentItem.ParkEvidence is null
                && currentItem.ContinuationEvidence is null
                && candidateItem.ParkEvidence is not null
                && candidateItem.ContinuationEvidence is null
                && currentActivation?.Status == GovernedLoopNodeExecutionStatus.Waiting
                && candidateActivation?.Status == GovernedLoopNodeExecutionStatus.Waiting
                && current.Frontier?.Payload.FrontierVersion == candidate.Frontier?.Payload.FrontierVersion
                && string.Equals(current.Frontier?.Payload.ContentHash, candidate.Frontier?.Payload.ContentHash, StringComparison.Ordinal);
            var attachedContinuation = currentItem.ParkEvidence is not null
                && string.Equals(currentItem.ParkEvidence.ContentHash, candidateItem.ParkEvidence?.ContentHash, StringComparison.Ordinal)
                && currentItem.ContinuationEvidence is null
                && candidateItem.ContinuationEvidence is { } continuation
                && currentActivation?.Status == GovernedLoopNodeExecutionStatus.Waiting
                && candidateActivation?.Status == GovernedLoopNodeExecutionStatus.Running
                && continuation.PreResumeFrontierVersion == current.Frontier?.Payload.FrontierVersion
                && string.Equals(continuation.PreResumeFrontierHash, current.Frontier?.Payload.ContentHash, StringComparison.Ordinal)
                && continuation.ResumedFrontierVersion == candidate.Frontier?.Payload.FrontierVersion
                && string.Equals(continuation.ResumedFrontierHash, candidate.Frontier?.Payload.ContentHash, StringComparison.Ordinal);
            if (!attachedPark && !attachedContinuation)
            {
                Add(errors, "invalid_wait_evidence_phase_advance", $"waitEvidence[{index}]", "A retained Wait may append only its checkpoint while Waiting or its continuation on the exact Waiting-to-Running successor.");
            }
        }

        foreach (var item in candidate.WaitEvidence.Skip(current.WaitEvidence.Count))
        {
            changed++;
            var currentActivation = item is null ? null : current.Frontier?.Payload.Nodes.ElementAtOrDefault(item.ActivationOrdinal);
            var activation = item is null ? null : candidate.Frontier?.Payload.Nodes.ElementAtOrDefault(item.ActivationOrdinal);
            if (item is null
                || item.ParkEvidence is not null
                || item.ContinuationEvidence is not null
                || currentActivation?.Status != GovernedLoopNodeExecutionStatus.Running
                || activation?.Status != GovernedLoopNodeExecutionStatus.Waiting
                || item.ParkedFrontierVersion != candidate.Frontier?.Payload.FrontierVersion
                || !string.Equals(item.ParkedFrontierHash, candidate.Frontier?.Payload.ContentHash, StringComparison.Ordinal))
            {
                Add(errors, "invalid_initial_wait_evidence_phase", "waitEvidence", "A new Wait record may retain only its exact Waiting-frontier coordinates; checkpoint and continuation evidence are later append-only phases.");
            }
        }

        if (changed > 1)
        {
            Add(errors, "multiple_wait_evidence_advances", "waitEvidence", "One lifecycle successor may advance at most one activation-scoped Wait evidence record.");
        }
    }

    private static void ValidateAppendOnlyHumanInputWaitingCheckpoints(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.HumanInputWaitingCheckpoints is null
            || candidate.HumanInputWaitingCheckpoints is null
            || candidate.HumanInputWaitingCheckpoints.Count < current.HumanInputWaitingCheckpoints.Count)
        {
            Add(errors, "human_input_waiting_checkpoint_history_truncated", "humanInputWaitingCheckpoints", "Persisted Human Input checkpoints are append-only.");
            return;
        }

        var advanced = 0;
        for (var index = 0; index < current.HumanInputWaitingCheckpoints.Count; index++)
        {
            var currentCheckpoint = current.HumanInputWaitingCheckpoints[index];
            var candidateCheckpoint = candidate.HumanInputWaitingCheckpoints[index];
            if (currentCheckpoint is null
                || candidateCheckpoint is null)
            {
                Add(errors, "human_input_waiting_checkpoint_history_changed", $"humanInputWaitingCheckpoints[{index}]", "Retained Human Input checkpoints must remain complete and cannot be removed.");
                continue;
            }

            if (string.Equals(currentCheckpoint.CheckpointHash, candidateCheckpoint.CheckpointHash, StringComparison.Ordinal))
            {
                continue;
            }

            if (!GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(currentCheckpoint, candidateCheckpoint).IsValid)
            {
                Add(errors, "human_input_waiting_checkpoint_transition_invalid", $"humanInputWaitingCheckpoints[{index}]", "A retained Human Input checkpoint may advance only through its exact append-only response-continuation transition.");
                continue;
            }

            advanced++;
        }

        if (advanced > 1)
        {
            Add(errors, "multiple_human_input_waiting_checkpoint_advances", "humanInputWaitingCheckpoints", "One optimistic run successor may advance at most one Human Input checkpoint.");
        }

        var appended = candidate.HumanInputWaitingCheckpoints.Skip(current.HumanInputWaitingCheckpoints.Count).ToArray();
        if (appended.Length > 1)
        {
            Add(errors, "multiple_human_input_waiting_checkpoints_appended", "humanInputWaitingCheckpoints", "One optimistic run successor may publish at most one Human Input checkpoint.");
            return;
        }

        if (appended.Length == 0)
        {
            return;
        }

        var checkpoint = appended[0];
        var currentActivation = checkpoint is null ? null : current.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        var candidateActivation = checkpoint is null ? null : candidate.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        if (checkpoint is null
            || currentActivation is null
            || candidateActivation is null
            || currentActivation.Descriptor.Kind != GovernedLoopNodeKind.HumanInput
            || candidateActivation.Descriptor.Kind != GovernedLoopNodeKind.HumanInput
            || currentActivation.Status != GovernedLoopNodeExecutionStatus.Running
            || candidateActivation.Status != GovernedLoopNodeExecutionStatus.Waiting
            || currentActivation.ActivationOrdinal != candidateActivation.ActivationOrdinal
            || currentActivation.VisitOrdinal != candidateActivation.VisitOrdinal
            || !string.Equals(currentActivation.NodeId, candidateActivation.NodeId, StringComparison.Ordinal)
            || currentActivation.Attempt != candidateActivation.Attempt
            || !string.Equals(currentActivation.AttemptOperationId, candidateActivation.AttemptOperationId, StringComparison.Ordinal)
            || checkpoint.Binding.FrontierVersion != candidate.Frontier?.Payload.FrontierVersion
            || !string.Equals(checkpoint.Binding.FrontierHash, candidate.Frontier?.Payload.ContentHash, StringComparison.Ordinal))
        {
            Add(errors, "invalid_human_input_waiting_checkpoint_publication", "humanInputWaitingCheckpoints", "A new Human Input checkpoint must be published in the same optimistic successor that parks its exact Running activation as Waiting.");
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

    private static void ValidateHumanReview(CustomLoopRunRecord run, List<CustomLoopValidationError> errors)
    {
        var reviewEventMarkers = run.Events?.Where(item => item?.Kind is CustomLoopRunEventKind.HumanReviewRequestAdmitted or CustomLoopRunEventKind.HumanReviewDecisionOperationRecorded or CustomLoopRunEventKind.HumanReviewContinuationReserved).ToArray() ?? [];
        if (run.HumanReview is null)
        {
            if (reviewEventMarkers.Length != 0 || run.Events?.Any(item => item is not null && (item.HumanReviewEvidence is not null || item.HumanReviewDecisionOperation is not null || item.HumanReviewContinuationReservation is not null)) == true)
            {
                Add(errors, "human_review_state_required", "humanReview", "Human Review events require the canonical Human Review state plane.");
            }

            return;
        }

        var state = run.HumanReview;
        var statePlaneArraysPresent = !state.OperationReceipts.IsDefault && !state.AcceptedDecisions.IsDefault && !state.LifecycleHistory.IsDefault;
        var evidencePresent = !state.Evidence.IsDefault;
        if (!statePlaneArraysPresent)
        {
            Add(errors, "human_review_state_plane_required", "humanReview", "Schema-1 Human Review state requires lifecycle history, operation receipts, and accepted decisions even when empty.");
        }

        if (!evidencePresent)
        {
            Add(errors, "human_review_evidence_required", "humanReview.evidence", "Schema-1 Human Review state requires its ordered evidence chain, including the admission artifact.");
        }

        var request = state.Request;
        var lifecycle = state.Lifecycle;
        var requestValid = request is not null && IsValidHumanReviewRequest(request);
        if (!requestValid)
        {
            Add(errors, "invalid_human_review_request", "humanReview.request", "The canonical Human Review request is required and must be valid.");
        }
        else if (!MatchesHumanReviewBinding(run, request!))
        {
            Add(errors, "human_review_request_frontier_mismatch", "humanReview.request.binding", "The Human Review request must bind the retained run and exact current ReviewBlocked frontier.");
        }

        var lifecycleValid = lifecycle is not null && requestValid && IsValidHumanReviewLifecycle(request, lifecycle);
        if (!lifecycleValid)
        {
            Add(errors, "invalid_human_review_lifecycle", "humanReview.lifecycle", "The canonical Human Review lifecycle is required and must be valid.");
        }
        else if (!statePlaneArraysPresent || state.LifecycleHistory.Length == 0)
        {
            if (statePlaneArraysPresent)
            {
                Add(errors, "human_review_state_plane_required", "humanReview", "Schema-1 Human Review state requires lifecycle history, operation receipts, and accepted decisions even when empty.");
            }
        }
        else if (!HasValidHumanReviewLifecycleHistory(request!, state, errors))
        {
            // Detailed errors are reported by the history validator.
        }
        else if (state.OperationReceipts.Length == 0 && (lifecycle!.Status != HumanReviewLifecycleStatus.Pending || lifecycle.LifecycleVersion != 1 || lifecycle.LastDecision is not null))
        {
            Add(errors, "invalid_human_review_initial_lifecycle", "humanReview.lifecycle", "Atomic admission requires the exact initial pending Human Review lifecycle with no decision.");
        }

        if (run.Status != CustomLoopRunStatus.Paused || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked)
        {
            Add(errors, "human_review_frontier_mismatch", "humanReview", "Human Review requires the nonterminal Paused and exact ReviewBlocked frontier posture.");
        }

        if (!statePlaneArraysPresent || !evidencePresent)
        {
            return;
        }

        if (state.Evidence.Length != state.OperationReceipts.Length + (state.ContinuationReservation is null ? 1 : 2))
        {
            Add(errors, "invalid_human_review_evidence_count", "humanReview.evidence", "Human Review evidence must retain admission plus one exact evidence artifact per receipt and reservation.");
        }

        var evidenceValid = true;
        string? previousHash = null;
        for (var index = 0; index < state.Evidence.Length; index++)
        {
            var evidence = state.Evidence[index];
            if (evidence is null
                || !requestValid
                || !IsValidHumanReviewEvidence(request, evidence)
                || (index == 0 && evidence.Kind != HumanReviewEvidenceKind.RequestAdmitted)
                || !string.Equals(evidence.PreviousEvidenceHash, previousHash, StringComparison.Ordinal))
            {
                Add(errors, "invalid_human_review_evidence", $"humanReview.evidence[{index}]", "Human Review evidence must be valid and form one exact append-only hash chain.");
                evidenceValid = false;
                continue;
            }

            previousHash = evidence.EvidenceHash;
        }

        if (!evidenceValid || !HasExactHumanReviewEventEvidenceBindings(run, request!, state))
        {
            Add(errors, "human_review_event_evidence_mismatch", "events", "Each Human Review event must carry its exact retained evidence and typed receipt or reservation reference.");
        }

        ValidateHumanReviewDecisionState(request!, state, errors);
    }

    private static bool MatchesHumanReviewBinding(CustomLoopRunRecord run, HumanReviewRequest request)
    {
        if (run.Frontier is not { } frontier || frontier.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked)
        {
            return false;
        }

        var blockedNodes = frontier.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked).Take(2).ToArray();
        var blockedNode = blockedNodes.Length == 1 ? blockedNodes[0] : null;
        return blockedNode is not null
            && string.Equals(request.Binding.WorkspaceId, frontier.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(request.Binding.RunId, run.Id, StringComparison.Ordinal)
            && string.Equals(request.Binding.GraphId, frontier.Binding.Revision.GraphId, StringComparison.Ordinal)
            && string.Equals(request.Binding.RevisionId, frontier.Binding.Revision.RevisionId, StringComparison.Ordinal)
            && string.Equals(request.Binding.RevisionHash, frontier.Binding.Revision.ExecutableHash, StringComparison.Ordinal)
            && request.Binding.FrontierVersion == frontier.Payload.FrontierVersion
            && string.Equals(request.Binding.FrontierHash, frontier.Payload.ContentHash, StringComparison.Ordinal)
            && string.Equals(request.Binding.NodeId, blockedNode.NodeId, StringComparison.Ordinal)
            && request.Binding.Attempt == blockedNode.Attempt
            && (request.Binding.ActivationOrdinal is null || request.Binding.ActivationOrdinal == blockedNode.ActivationOrdinal)
            && (request.Binding.VisitOrdinal is null || request.Binding.VisitOrdinal == blockedNode.VisitOrdinal);
    }

    private static bool IsValidHumanReviewRequest(HumanReviewRequest request)
    {
        try
        {
            return HumanReviewContractValidator.ValidateRequest(request).IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidHumanReviewLifecycle(HumanReviewRequest? request, HumanReviewLifecycle lifecycle)
    {
        try
        {
            return HumanReviewContractValidator.ValidateLifecycle(request, lifecycle).IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidHumanReviewEvidence(HumanReviewRequest? request, HumanReviewEvidence evidence)
    {
        try
        {
            return HumanReviewContractValidator.ValidateEvidence(request, evidence).IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidHumanReviewLifecycleHistory(HumanReviewRequest request, HumanReviewRunState state, List<CustomLoopValidationError> errors)
    {
        var history = state.LifecycleHistory;
        if (history.Length > HumanReviewContractLimits.MaxLifecycleHistory)
        {
            Add(errors, "human_review_lifecycle_history_limit", "humanReview.lifecycleHistory", "Lifecycle history exceeds the bounded schema-1 limit.");
            return false;
        }
        var first = history[0];
        if (first is null || !IsValidHumanReviewLifecycle(request, first) || first.Status != HumanReviewLifecycleStatus.Pending || first.LifecycleVersion != 1 || first.LastDecision is not null)
        {
            Add(errors, "invalid_human_review_initial_lifecycle", "humanReview.lifecycleHistory[0]", "Lifecycle history must begin with the admitted pending lifecycle.");
            return false;
        }
        for (var index = 1; index < history.Length; index++)
        {
            var previous = history[index - 1];
            var current = history[index];
            if (current is null || previous is null || !IsValidHumanReviewLifecycle(request, current) || current.LifecycleVersion != previous.LifecycleVersion + 1 || current.UpdatedAtUtc < previous.UpdatedAtUtc || !string.Equals(current.PreviousLifecycleHash, previous.LifecycleHash, StringComparison.Ordinal))
            {
                Add(errors, "invalid_human_review_lifecycle_history", $"humanReview.lifecycleHistory[{index}]", "Lifecycle history must be contiguous, hash-linked, valid, and time-monotonic.");
                return false;
            }
        }
        if (!string.Equals(state.Lifecycle.LifecycleHash, history[^1].LifecycleHash, StringComparison.Ordinal))
        {
            Add(errors, "human_review_lifecycle_head_mismatch", "humanReview.lifecycle", "The lifecycle head must equal the final append-only lifecycle history item.");
            return false;
        }
        return true;
    }

    private static void ValidateHumanReviewDecisionState(HumanReviewRequest request, HumanReviewRunState state, List<CustomLoopValidationError> errors)
    {
        if (state.OperationReceipts.Length > HumanReviewContractLimits.MaxDecisionOperationReceipts || state.AcceptedDecisions.Length > HumanReviewContractLimits.MaxAcceptedDecisions)
        {
            Add(errors, "human_review_decision_cardinality_exceeded", "humanReview", "Decision state exceeds bounded schema-1 receipt or accepted-decision limits.");
        }
        var operations = new HashSet<string>(StringComparer.Ordinal);
        var acceptedOperations = new HashSet<string>(StringComparer.Ordinal);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var decisionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in state.Evidence)
        {
            if (evidence is null || !evidenceIds.Add(evidence.EvidenceId)) Add(errors, "duplicate_human_review_evidence_identity", "humanReview.evidence", "Evidence identities must be unique.");
        }
        foreach (var receipt in state.OperationReceipts)
        {
            if (receipt is null || !IsValidHumanReviewReceipt(request, receipt) || !operations.Add(receipt.DecisionOperationId)) Add(errors, "invalid_human_review_receipt", "humanReview.operationReceipts", "Receipts must be valid, unique, and append-only.");
        }
        foreach (var decision in state.AcceptedDecisions)
        {
            if (decision is null || !IsValidHumanReviewDecision(request, decision) || !acceptedOperations.Add(decision.DecisionOperationId) || !decisionIds.Add(decision.DecisionId)
                || state.OperationReceipts.Count(receipt => receipt is not null && string.Equals(receipt.DecisionOperationId, decision.DecisionOperationId, StringComparison.Ordinal) && SameDecisionReference(receipt.Decision, decision)) != 1)
            {
                Add(errors, "invalid_human_review_accepted_decision", "humanReview.acceptedDecisions", "Accepted decisions must be valid and have one exact receipt.");
            }
        }
        var terminals = state.AcceptedDecisions.Where(item => item is not null && item.Kind != HumanReviewDecisionKind.RequestInformation).ToArray();
        if (state.AcceptedTerminalDecision is not null && (!IsValidHumanReviewDecision(request, state.AcceptedTerminalDecision) || !state.AcceptedDecisions.Any(item => SameDecision(item, state.AcceptedTerminalDecision))))
        {
            Add(errors, "invalid_human_review_terminal_decision", "humanReview.acceptedTerminalDecision", "The terminal decision must be independently valid and exactly equal to one retained accepted decision.");
        }
        if (terminals.Length > 1 || (terminals.Length == 1 && (state.AcceptedTerminalDecision is null || !SameDecision(terminals[0], state.AcceptedTerminalDecision))) || (terminals.Length == 0 && state.AcceptedTerminalDecision is not null))
        {
            Add(errors, "invalid_human_review_terminal_decision", "humanReview.acceptedTerminalDecision", "At most one terminal decision may be retained and the optional terminal field must reference it exactly.");
        }
        else if (terminals.Length == 1 && !SameDecision(state.AcceptedDecisions[^1], terminals[0]))
        {
            Add(errors, "human_review_terminal_not_last", "humanReview.acceptedDecisions", "An accepted terminal decision must be the last accepted decision and cannot be followed by another accepted outcome.");
        }
        else if (terminals.Length == 1)
        {
            var terminalReceiptIndex = Array.FindIndex(state.OperationReceipts.ToArray(), receipt => receipt is not null && SameDecisionReference(receipt.Decision, terminals[0]));
            if (terminalReceiptIndex < 0 || state.OperationReceipts.Skip(terminalReceiptIndex + 1).Any(receipt => receipt is not null && receipt.Disposition is (HumanReviewDecisionOperationDisposition.Accepted or HumanReviewDecisionOperationDisposition.InformationRequested)))
            {
                Add(errors, "human_review_terminal_receipt_outcome", "humanReview.operationReceipts", "An accepted terminal decision cannot be followed by another accepted or information-requested operation, but later nonaccepted audit receipts remain append-only.");
            }
        }
        if (state.ContinuationReservation is not null && (!IsValidHumanReviewReservation(request, state.ContinuationReservation) || state.AcceptedTerminalDecision?.Kind != HumanReviewDecisionKind.Approve || !string.Equals(state.ContinuationReservation.Decision.DecisionHash, state.AcceptedTerminalDecision.DecisionHash, StringComparison.Ordinal)))
        {
            Add(errors, "invalid_human_review_reservation", "humanReview.continuationReservation", "Only the exact accepted approval may have one valid continuation reservation.");
        }
        else if (state.ContinuationReservation is not null && !SameDecisionReference(state.ContinuationReservation.Decision, state.AcceptedTerminalDecision!))
        {
            Add(errors, "human_review_reservation_decision_substitution", "humanReview.continuationReservation.decision", "Reservation must reference the exact independently validated accepted approval.");
        }
        if (state.Continuation is not null)
        {
            if (state.ContinuationReservation is null
                || state.AcceptedTerminalDecision?.Kind != HumanReviewDecisionKind.Approve
                || !HumanReviewContinuationContractValidator.ValidateState(request, state.ContinuationReservation, state.Continuation).IsValid)
            {
                Add(errors, "invalid_human_review_continuation", "humanReview.continuation", "Continuation state requires the exact accepted approval reservation and a valid append-only continuation state machine.");
            }
        }
        ValidateHumanReviewReceiptEvidenceCausality(state, errors);
        ValidateHumanReviewLifecycleCausality(state, errors);
    }

    private static bool HasExactHumanReviewEventEvidenceBindings(CustomLoopRunRecord run, HumanReviewRequest request, HumanReviewRunState state)
    {
        if (run.Events.Any(item => item is not null && !HasRequiredHumanReviewEventPayload(item))) return false;
        var reviewEvents = run.Events.Where(item => item?.HumanReviewEvidence is not null).ToArray();
        if (reviewEvents.Length != state.Evidence.Length) return false;
        for (var index = 0; index < state.Evidence.Length; index++)
        {
            var evidence = state.Evidence[index]; var item = reviewEvents[index];
            if (item is null || evidence is null || !IsValidHumanReviewEvidence(request, item.HumanReviewEvidence!) || !string.Equals(item.HumanReviewEvidence?.EvidenceHash, evidence.EvidenceHash, StringComparison.Ordinal) || item.TimestampUtc != evidence.RecordedAtUtc) return false;
            if (index == 0 && (item.Kind != CustomLoopRunEventKind.HumanReviewRequestAdmitted || item.HumanReviewDecisionOperation is not null || item.HumanReviewContinuationReservation is not null)) return false;
            if (evidence.DecisionOperation is not null && (item.Kind != CustomLoopRunEventKind.HumanReviewDecisionOperationRecorded || !Equals(item.HumanReviewDecisionOperation, evidence.DecisionOperation) || item.HumanReviewContinuationReservation is not null)) return false;
            if (evidence.ContinuationReservation is not null && (item.Kind != CustomLoopRunEventKind.HumanReviewContinuationReserved || !Equals(item.HumanReviewContinuationReservation, evidence.ContinuationReservation) || item.HumanReviewDecisionOperation is not null)) return false;
            if (evidence.DecisionOperation is null && evidence.ContinuationReservation is null && (item.HumanReviewDecisionOperation is not null || item.HumanReviewContinuationReservation is not null)) return false;
        }
        return !run.Events.Any(item => item is not null && (item.HumanReviewDecisionOperation is not null || item.HumanReviewContinuationReservation is not null) && item.HumanReviewEvidence is null);
    }

    private static bool HasRequiredHumanReviewEventPayload(CustomLoopRunEvent item)
        => item.Kind switch
        {
            CustomLoopRunEventKind.HumanReviewRequestAdmitted => item.HumanReviewEvidence is not null && item.HumanReviewDecisionOperation is null && item.HumanReviewContinuationReservation is null,
            CustomLoopRunEventKind.HumanReviewDecisionOperationRecorded => item.HumanReviewEvidence is not null && item.HumanReviewDecisionOperation is not null && item.HumanReviewContinuationReservation is null,
            CustomLoopRunEventKind.HumanReviewContinuationReserved => item.HumanReviewEvidence is not null && item.HumanReviewDecisionOperation is null && item.HumanReviewContinuationReservation is not null,
            _ => true,
        };

    private static void ValidateHumanReviewReceiptEvidenceCausality(HumanReviewRunState state, List<CustomLoopValidationError> errors)
    {
        var operationEvidence = state.Evidence.Where(item => item?.DecisionOperation is not null).ToArray();
        if (operationEvidence.Length != state.OperationReceipts.Length)
        {
            Add(errors, "human_review_receipt_evidence_cardinality", "humanReview", "Every receipt must have exactly one typed evidence artifact.");
            return;
        }
        DateTimeOffset? previousReceiptAtUtc = null;
        for (var index = 0; index < state.OperationReceipts.Length; index++)
        {
            var receipt = state.OperationReceipts[index];
            var evidence = operationEvidence[index];
            if (receipt is null || evidence is null)
            {
                Add(errors, "invalid_human_review_receipt", "humanReview.operationReceipts", "Receipts and their evidence must be present before their causal bindings are evaluated.");
                return;
            }

            var matches = operationEvidence.Where(candidate => SameReceiptReference(candidate!.DecisionOperation!, receipt)).ToArray();
            var decision = AtMostOne(state.AcceptedDecisions.Where(candidate => candidate is not null && SameDecisionReference(receipt.Decision, candidate)));
            if (previousReceiptAtUtc is { } previous && receipt.RecordedAtUtc < previous)
            {
                Add(errors, "human_review_receipt_chronology", "humanReview.operationReceipts", "Decision-operation receipts must be retained in nondecreasing trusted-time order.");
            }
            previousReceiptAtUtc = receipt.RecordedAtUtc;
            if (matches.Length != 1 || !EvidenceMatchesReceipt(matches[0]!, receipt) || matches[0]!.RecordedAtUtc < receipt.RecordedAtUtc)
            {
                Add(errors, "human_review_receipt_evidence_mismatch", "humanReview.evidence", "Each receipt must bind exactly one matching evidence disposition and nondecreasing evidence timestamp.");
            }
            if (!SameReceiptReference(evidence.DecisionOperation!, receipt))
            {
                Add(errors, "human_review_receipt_evidence_order", "humanReview.evidence", "The receipt ledger, operation-evidence ledger, and their exact events must retain the same append-only operation order.");
            }
            if (receipt.Decision is not null && (decision is null || decision.DecidedAtUtc > receipt.RecordedAtUtc))
            {
                Add(errors, "human_review_receipt_decision_chronology", "humanReview.operationReceipts", "An accepted decision must exactly resolve its receipt and cannot postdate it.");
            }
        }
        if (state.ContinuationReservation is not null)
        {
            var reservationEvidence = state.Evidence.Where(item => item?.ContinuationReservation is not null).ToArray();
            var approvalReceipt = AtMostOne(state.OperationReceipts.Where(receipt => receipt is not null && SameDecisionReference(receipt.Decision, state.ContinuationReservation.Decision)));
            var approvalLifecycle = AtMostOne(state.LifecycleHistory.Where(lifecycle => lifecycle is not null && SameDecisionReference(lifecycle.LastDecision, state.ContinuationReservation.Decision)));
            if (reservationEvidence.Length != 1
                || !string.Equals(reservationEvidence[0]!.ContinuationReservation!.ReservationId, state.ContinuationReservation.ReservationId, StringComparison.Ordinal)
                || !string.Equals(reservationEvidence[0]!.ContinuationReservation!.ReservationHash, state.ContinuationReservation.ReservationHash, StringComparison.Ordinal)
                || !SameDecisionReference(reservationEvidence[0]!.Decision, state.ContinuationReservation.Decision)
                || approvalReceipt is null
                || approvalLifecycle is null
                || state.ContinuationReservation.ReservedAtUtc < approvalReceipt.RecordedAtUtc
                || state.ContinuationReservation.ReservedAtUtc < approvalLifecycle.UpdatedAtUtc
                || reservationEvidence[0]!.RecordedAtUtc < state.ContinuationReservation.ReservedAtUtc)
            {
                Add(errors, "human_review_reservation_evidence_mismatch", "humanReview.evidence", "The reservation must exactly bind approval receipt, lifecycle, evidence, and nondecreasing trusted timestamps.");
            }
        }
    }

    private static void ValidateHumanReviewLifecycleCausality(HumanReviewRunState state, List<CustomLoopValidationError> errors)
    {
        var accepted = state.AcceptedDecisions;
        var expiryReceipts = state.OperationReceipts.Where(item => item is not null && item.Disposition == HumanReviewDecisionOperationDisposition.Expired).ToArray();
        var terminal = AtMostOne(accepted.Where(item => item is not null && item.Kind != HumanReviewDecisionKind.RequestInformation));
        var terminalReceiptIndex = terminal is null ? -1 : Array.FindIndex(state.OperationReceipts.ToArray(), item => item is not null && SameDecisionReference(item.Decision, terminal));
        var expiryLifecycleRequired = expiryReceipts.Length != 0 && terminal is null;
        var expectedHistoryLength = checked(accepted.Length + 1 + (expiryLifecycleRequired ? 1 : 0));
        if (state.LifecycleHistory.Length != expectedHistoryLength)
        {
            Add(errors, "human_review_lifecycle_history_causality", "humanReview.lifecycleHistory", "Lifecycle history must contain the initial pending entry, one ordered entry per accepted decision, and exactly one expiry entry when expiry closes a nonterminal review.");
            return;
        }

        for (var index = 0; index < accepted.Length; index++)
        {
            var decision = accepted[index];
            var lifecycle = state.LifecycleHistory[index + 1];
            var receipt = AtMostOne(state.OperationReceipts.Where(item => item is not null && SameDecisionReference(item.Decision, decision)));
            var evidence = AtMostOne(state.Evidence.Where(item => item?.DecisionOperation is not null && receipt is not null && SameReceiptReference(item.DecisionOperation, receipt)));
            if (decision is null
                || lifecycle is null
                || receipt is null
                || evidence is null
                || lifecycle.Status != LifecycleStatusFor(decision.Kind)
                || !SameDecisionReference(lifecycle.LastDecision, decision)
                || lifecycle.UpdatedAtUtc < decision.DecidedAtUtc
                || lifecycle.UpdatedAtUtc < evidence.RecordedAtUtc)
            {
                Add(errors, "human_review_lifecycle_decision_causality", "humanReview.lifecycleHistory", "Each accepted decision must have one ordered exact lifecycle successor after its receipt evidence.");
            }
        }

        var head = state.LifecycleHistory[^1];
        if (head is null)
        {
            Add(errors, "human_review_lifecycle_history_causality", "humanReview.lifecycleHistory", "Lifecycle causality requires a retained non-null lifecycle head.");
            return;
        }

        if (expiryLifecycleRequired)
        {
            var expiry = expiryReceipts[0];
            var evidence = AtMostOne(state.Evidence.Where(item => item?.DecisionOperation is not null && SameReceiptReference(item.DecisionOperation, expiry)));
            if (head.Status != HumanReviewLifecycleStatus.Expired
                || head.LastDecision is not null
                || head.UpdatedAtUtc < expiry.RecordedAtUtc
                || evidence is null
                || head.UpdatedAtUtc < evidence.RecordedAtUtc
                || expiryReceipts.Skip(1).Any(receipt => receipt.RecordedAtUtc < head.UpdatedAtUtc))
            {
                Add(errors, "human_review_expiry_receipt_required", "humanReview.lifecycle", "Expired lifecycle posture requires the first exact expired receipt and permits only later nondecreasing expired audit receipts.");
            }
        }
        else if (expiryReceipts.Length != 0 && (terminalReceiptIndex < 0 || Array.FindIndex(state.OperationReceipts.ToArray(), item => item is not null && item.Disposition == HumanReviewDecisionOperationDisposition.Expired) < terminalReceiptIndex))
        {
            Add(errors, "human_review_expiry_chronology", "humanReview.operationReceipts", "An expired audit receipt may follow an accepted terminal decision without changing its terminal lifecycle, but cannot precede that winner.");
        }
        else if (accepted.Length == 0 && head.Status != HumanReviewLifecycleStatus.Pending)
        {
            Add(errors, "human_review_lifecycle_without_accepted_decision", "humanReview.lifecycle", "Only the initial pending lifecycle may exist without an accepted decision or exact expiry receipt.");
        }
    }

    private static HumanReviewLifecycleStatus LifecycleStatusFor(HumanReviewDecisionKind kind)
        => kind switch
        {
            HumanReviewDecisionKind.RequestInformation => HumanReviewLifecycleStatus.AwaitingInformation,
            HumanReviewDecisionKind.Approve => HumanReviewLifecycleStatus.Approved,
            HumanReviewDecisionKind.Reject => HumanReviewLifecycleStatus.Rejected,
            HumanReviewDecisionKind.Cancel => HumanReviewLifecycleStatus.Cancelled,
            _ => HumanReviewLifecycleStatus.Unknown,
        };

    private static T? AtMostOne<T>(IEnumerable<T> values) where T : class
    {
        var candidates = values.Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool EvidenceMatchesReceipt(HumanReviewEvidence evidence, HumanReviewDecisionOperationReceipt receipt)
        => evidence.Kind switch
        {
            HumanReviewEvidenceKind.DecisionAccepted => receipt.Disposition == HumanReviewDecisionOperationDisposition.Accepted && SameDecisionReference(evidence.Decision, receipt.Decision),
            HumanReviewEvidenceKind.InformationRequested => receipt.Disposition == HumanReviewDecisionOperationDisposition.InformationRequested && SameDecisionReference(evidence.Decision, receipt.Decision),
            HumanReviewEvidenceKind.DecisionConflict => receipt.Disposition == HumanReviewDecisionOperationDisposition.Conflict && evidence.Decision is null,
            HumanReviewEvidenceKind.DecisionDenied => receipt.Disposition == HumanReviewDecisionOperationDisposition.Denied && evidence.Decision is null,
            HumanReviewEvidenceKind.DecisionExpired => receipt.Disposition == HumanReviewDecisionOperationDisposition.Expired && evidence.Decision is null,
            _ => false
        };

    private static bool SameReceiptReference(HumanReviewDecisionOperationReference reference, HumanReviewDecisionOperationReceipt receipt)
        => string.Equals(reference.DecisionOperationId, receipt.DecisionOperationId, StringComparison.Ordinal) && string.Equals(reference.ProposalHash, receipt.ProposalHash, StringComparison.Ordinal) && reference.Disposition == receipt.Disposition && string.Equals(reference.ReceiptHash, receipt.ReceiptHash, StringComparison.Ordinal);

    private static bool SameDecision(HumanReviewDecision? left, HumanReviewDecision? right)
        => left is not null && right is not null && string.Equals(left.DecisionHash, right.DecisionHash, StringComparison.Ordinal) && left.SchemaVersion == right.SchemaVersion && string.Equals(left.DecisionId, right.DecisionId, StringComparison.Ordinal) && string.Equals(left.DecisionOperationId, right.DecisionOperationId, StringComparison.Ordinal) && SameDecisionReference(new HumanReviewDecisionReference(left.DecisionId, left.DecisionOperationId, left.Kind, left.DecisionHash), right);

    private static bool SameDecisionReference(HumanReviewDecisionReference? left, HumanReviewDecision? right)
        => left is not null && right is not null && string.Equals(left.DecisionId, right.DecisionId, StringComparison.Ordinal) && string.Equals(left.DecisionOperationId, right.DecisionOperationId, StringComparison.Ordinal) && left.Kind == right.Kind && string.Equals(left.DecisionHash, right.DecisionHash, StringComparison.Ordinal);

    private static bool SameDecisionReference(HumanReviewDecisionReference? left, HumanReviewDecisionReference? right)
        => left is not null && right is not null && string.Equals(left.DecisionId, right.DecisionId, StringComparison.Ordinal) && string.Equals(left.DecisionOperationId, right.DecisionOperationId, StringComparison.Ordinal) && left.Kind == right.Kind && string.Equals(left.DecisionHash, right.DecisionHash, StringComparison.Ordinal);

    private static bool IsValidHumanReviewDecision(HumanReviewRequest request, HumanReviewDecision decision) { try { return HumanReviewContractValidator.ValidateDecision(request, decision).IsValid; } catch { return false; } }
    private static bool IsValidHumanReviewReceipt(HumanReviewRequest request, HumanReviewDecisionOperationReceipt receipt) { try { return HumanReviewContractValidator.ValidateDecisionOperationReceipt(request, receipt).IsValid; } catch { return false; } }
    private static bool IsValidHumanReviewReservation(HumanReviewRequest request, HumanReviewContinuationReservation reservation) { try { return HumanReviewContractValidator.ValidateContinuationReservation(request, reservation).IsValid; } catch { return false; } }

    private static void ValidateAppendOnlyHumanReview(CustomLoopRunRecord current, CustomLoopRunRecord candidate, List<CustomLoopValidationError> errors)
    {
        if (current.HumanReview is null)
        {
            return;
        }

        if (candidate.HumanReview is null
            || current.HumanReview.Request is null
            || candidate.HumanReview.Request is null
            || current.HumanReview.Lifecycle is null
            || candidate.HumanReview.Lifecycle is null
            || current.HumanReview.LifecycleHistory.IsDefault
            || current.HumanReview.Evidence.IsDefault
            || current.HumanReview.OperationReceipts.IsDefault
            || current.HumanReview.AcceptedDecisions.IsDefault
            || candidate.HumanReview.LifecycleHistory.IsDefault
            || candidate.HumanReview.Evidence.IsDefault
            || candidate.HumanReview.OperationReceipts.IsDefault
            || candidate.HumanReview.AcceptedDecisions.IsDefault
            || !string.Equals(current.HumanReview.Request.RequestHash, candidate.HumanReview.Request.RequestHash, StringComparison.Ordinal)
            || candidate.HumanReview.LifecycleHistory.Length < current.HumanReview.LifecycleHistory.Length
            || candidate.HumanReview.Evidence.Length < current.HumanReview.Evidence.Length
            || candidate.HumanReview.OperationReceipts.Length < current.HumanReview.OperationReceipts.Length
            || candidate.HumanReview.AcceptedDecisions.Length < current.HumanReview.AcceptedDecisions.Length)
        {
            Add(errors, "human_review_history_changed", "humanReview", "The admitted Human Review request, initial lifecycle, and evidence cannot be removed, extended, or rewritten in this slice.");
            return;
        }

        for (var index = 0; index < current.HumanReview.Evidence.Length; index++)
        {
            var currentEvidence = current.HumanReview.Evidence[index];
            var candidateEvidence = candidate.HumanReview.Evidence[index];
            if (currentEvidence is null || candidateEvidence is null || !string.Equals(currentEvidence.EvidenceHash, candidateEvidence.EvidenceHash, StringComparison.Ordinal))
            {
                Add(errors, "human_review_evidence_changed", $"humanReview.evidence[{index}]", "Previously retained Human Review evidence is immutable.");
            }
        }

        if (!current.HumanReview.LifecycleHistory.Select((item, index) => string.Equals(item?.LifecycleHash, candidate.HumanReview.LifecycleHistory[index]?.LifecycleHash, StringComparison.Ordinal)).All(value => value)
            || !current.HumanReview.OperationReceipts.Select((item, index) => string.Equals(item?.ReceiptHash, candidate.HumanReview.OperationReceipts[index]?.ReceiptHash, StringComparison.Ordinal)).All(value => value)
            || !current.HumanReview.AcceptedDecisions.Select((item, index) => string.Equals(item?.DecisionHash, candidate.HumanReview.AcceptedDecisions[index]?.DecisionHash, StringComparison.Ordinal)).All(value => value)
            || (current.HumanReview.AcceptedTerminalDecision is not null && !string.Equals(current.HumanReview.AcceptedTerminalDecision.DecisionHash, candidate.HumanReview.AcceptedTerminalDecision?.DecisionHash, StringComparison.Ordinal))
            || (current.HumanReview.ContinuationReservation is not null && !string.Equals(current.HumanReview.ContinuationReservation.ReservationHash, candidate.HumanReview.ContinuationReservation?.ReservationHash, StringComparison.Ordinal)))
        {
            Add(errors, "human_review_history_changed", "humanReview", "Previously authenticated Human Review state may only be extended.");
        }

        var currentContinuation = current.HumanReview.Continuation;
        var candidateContinuation = candidate.HumanReview.Continuation;
        if (currentContinuation is not null && candidateContinuation is null)
        {
            Add(errors, "human_review_continuation_removed", "humanReview.continuation", "Published Human Review continuation state is immutable and cannot be removed.");
        }
        else if (candidateContinuation is not null)
        {
            var continuationTransition = HumanReviewContinuationStateTransitionValidator.ValidateTransition(
                current.HumanReview.Request,
                current.HumanReview.ContinuationReservation,
                currentContinuation,
                candidateContinuation);
            if (!continuationTransition.IsValid)
            {
                Add(errors, "invalid_human_review_continuation_transition", "humanReview.continuation", "Continuation publication, claim, completion, retirement, and replay must use one exact append-only state transition.");
            }
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
            && left.ControlExpectedLifecycleVersion == right.ControlExpectedLifecycleVersion
            && Equals(left.SequentialNodeEvidence, right.SequentialNodeEvidence)
            && string.Equals(left.PureNodeOutcomeJson, right.PureNodeOutcomeJson, StringComparison.Ordinal)
            && string.Equals(left.WaitContinuationEvidenceHash, right.WaitContinuationEvidenceHash, StringComparison.Ordinal)
            && string.Equals(left.ModelExecutionEvidence?.ContentHash, right.ModelExecutionEvidence?.ContentHash, StringComparison.Ordinal)
            && string.Equals(left.FailureEvidence?.ContentHash, right.FailureEvidence?.ContentHash, StringComparison.Ordinal)
            && string.Equals(left.RetryState?.ContentHash, right.RetryState?.ContentHash, StringComparison.Ordinal)
            && string.Equals(left.HumanReviewEvidence?.EvidenceHash, right.HumanReviewEvidence?.EvidenceHash, StringComparison.Ordinal)
            && Equals(left.HumanReviewDecisionOperation, right.HumanReviewDecisionOperation)
            && Equals(left.HumanReviewContinuationReservation, right.HumanReviewContinuationReservation);
    }

    private static void ValidateModelExecutionEvidence(
        CustomLoopRunEvent item,
        string field,
        CustomLoopRunRecord run,
        List<CustomLoopValidationError> errors)
    {
        if (item.ModelExecutionEvidence is not { } evidence)
        {
            return;
        }
        if (item.Kind is not CustomLoopRunEventKind.NodeOutcomeObserved and not CustomLoopRunEventKind.NodeAttemptCompleted)
        {
            Add(errors, "unexpected_model_execution_evidence", $"{field}.modelExecutionEvidence", "Only completed Inference outcome evidence may retain model execution evidence.");
            return;
        }
        if (!GovernedModelContractValidator.IsValid(evidence)
            || !string.Equals(item.Provider, evidence.ProviderId, StringComparison.Ordinal)
            || !string.Equals(item.Model, evidence.ModelId, StringComparison.Ordinal))
        {
            Add(errors, "invalid_model_execution_evidence", $"{field}.modelExecutionEvidence", "Model execution evidence must be canonical and match the event provider/model projection.");
        }
        var entries = run.SequentialAdapterBinding?.AdmissionReceipt.Evidence.ModelRoutingAdmission.Entries
            .Where(entry => string.Equals(entry.NodeId, item.StepId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (entries is not { Length: 1 }
            || !string.Equals(evidence.ProfileId.Value, entries[0].Primary.Capability.DescriptorIdentity.Id.Value, StringComparison.Ordinal)
            || !string.Equals(evidence.ProfilePinHash, entries[0].Primary.ContentHash, StringComparison.Ordinal)
            || !string.Equals(evidence.ConfigurationHash, entries[0].Primary.Metadata.ConfigurationHash, StringComparison.Ordinal))
        {
            Add(errors, "model_execution_admission_mismatch", $"{field}.modelExecutionEvidence", "Model execution evidence must cite the exact canonical routing admission for this Inference node.");
        }
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

    private static bool CheckpointsEqual(CustomLoopRunCheckpoint? left, CustomLoopRunCheckpoint? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && left.Iteration == right.Iteration
            && left.NextStepIndex == right.NextStepIndex
            && left.AcceptedRepeatCount == right.AcceptedRepeatCount
            && left.PendingExitDecision == right.PendingExitDecision
            && left.EarlierRetainedOutputs is not null
            && right.EarlierRetainedOutputs is not null
            && left.EarlierRetainedOutputs.SequenceEqual(right.EarlierRetainedOutputs)
            && Equals(left.PreviousIterationResult, right.PreviousIterationResult)
            && Equals(left.CurrentIterationResult, right.CurrentIterationResult)
            && left.ToolRequestsUsed == right.ToolRequestsUsed
            && left.LastCommittedSequence == right.LastCommittedSequence;
    }

    private static bool FrontiersEqual(GovernedLoopFrontierPosture? left, GovernedLoopFrontierPosture? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
            && Equals(left.Binding, right.Binding)
            && string.Equals(left.GraphArtifactHash, right.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(left.GraphLayoutHash, right.GraphLayoutHash, StringComparison.Ordinal)
            && string.Equals(left.AdmissionReceiptHash, right.AdmissionReceiptHash, StringComparison.Ordinal)
            && string.Equals(left.Payload.ContentHash, right.Payload.ContentHash, StringComparison.Ordinal);
    }

    private static bool WaitEvidenceEqual(
        IReadOnlyList<GovernedLoopWaitExecutionEvidence>? left,
        IReadOnlyList<GovernedLoopWaitExecutionEvidence>? right)
        => left is not null
            && right is not null
            && left.Count == right.Count
            && left.Select(item => item?.ContentHash).SequenceEqual(right.Select(item => item?.ContentHash), StringComparer.Ordinal);

    private static bool HumanInputWaitingCheckpointsEqual(
        IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint>? left,
        IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint>? right)
        => left is not null
            && right is not null
            && left.Count == right.Count
            && left.Select(item => item?.CheckpointHash).SequenceEqual(right.Select(item => item?.CheckpointHash), StringComparer.Ordinal);

    private static bool HumanReviewStatesEqual(HumanReviewRunState? left, HumanReviewRunState? right)
        => ReferenceEquals(left, right)
            || left is not null
            && right is not null
            && string.Equals(left.Request?.RequestHash, right.Request?.RequestHash, StringComparison.Ordinal)
            && string.Equals(left.Lifecycle?.LifecycleHash, right.Lifecycle?.LifecycleHash, StringComparison.Ordinal)
            && !left.Evidence.IsDefault
            && !right.Evidence.IsDefault
            && left.Evidence.Length == right.Evidence.Length
            && left.Evidence.Select(item => item?.EvidenceHash).SequenceEqual(right.Evidence.Select(item => item?.EvidenceHash), StringComparer.Ordinal)
            && left.LifecycleHistory.Select(item => item?.LifecycleHash).SequenceEqual(right.LifecycleHistory.Select(item => item?.LifecycleHash), StringComparer.Ordinal)
            && left.OperationReceipts.Select(item => item?.ReceiptHash).SequenceEqual(right.OperationReceipts.Select(item => item?.ReceiptHash), StringComparer.Ordinal)
            && left.AcceptedDecisions.Select(item => item?.DecisionHash).SequenceEqual(right.AcceptedDecisions.Select(item => item?.DecisionHash), StringComparer.Ordinal)
            && string.Equals(left.AcceptedTerminalDecision?.DecisionHash, right.AcceptedTerminalDecision?.DecisionHash, StringComparison.Ordinal)
            && string.Equals(left.ContinuationReservation?.ReservationHash, right.ContinuationReservation?.ReservationHash, StringComparison.Ordinal)
            && string.Equals(left.Continuation?.StateHash, right.Continuation?.StateHash, StringComparison.Ordinal);

    private static bool HasHumanReviewPrefix(HumanReviewRunState? expectedPrefix, HumanReviewRunState? actual)
        => expectedPrefix is null
            || actual is not null
            && string.Equals(expectedPrefix.Request?.RequestHash, actual.Request?.RequestHash, StringComparison.Ordinal)
            && expectedPrefix.LifecycleHistory.Length <= actual.LifecycleHistory.Length
            && !expectedPrefix.Evidence.IsDefault
            && !actual.Evidence.IsDefault
            && expectedPrefix.Evidence.Length <= actual.Evidence.Length
            && expectedPrefix.Evidence.Select((item, index) => string.Equals(item?.EvidenceHash, actual.Evidence[index]?.EvidenceHash, StringComparison.Ordinal)).All(value => value)
            && expectedPrefix.LifecycleHistory.Select((item, index) => string.Equals(item?.LifecycleHash, actual.LifecycleHistory[index]?.LifecycleHash, StringComparison.Ordinal)).All(value => value)
            && expectedPrefix.OperationReceipts.Length <= actual.OperationReceipts.Length
            && expectedPrefix.OperationReceipts.Select((item, index) => string.Equals(item?.ReceiptHash, actual.OperationReceipts[index]?.ReceiptHash, StringComparison.Ordinal)).All(value => value)
            && expectedPrefix.AcceptedDecisions.Length <= actual.AcceptedDecisions.Length
            && expectedPrefix.AcceptedDecisions.Select((item, index) => string.Equals(item?.DecisionHash, actual.AcceptedDecisions[index]?.DecisionHash, StringComparison.Ordinal)).All(value => value)
            && (expectedPrefix.Continuation is null
                || actual.Continuation is not null
                && HumanReviewContinuationStateTransitionValidator.ValidateTransition(
                    expectedPrefix.Request,
                    expectedPrefix.ContinuationReservation,
                    expectedPrefix.Continuation,
                    actual.Continuation).IsValid);

    private static bool HasWaitEvidencePrefix(
        IReadOnlyList<GovernedLoopWaitExecutionEvidence>? expectedPrefix,
        IReadOnlyList<GovernedLoopWaitExecutionEvidence>? actual)
        => expectedPrefix is not null
            && actual is not null
            && expectedPrefix.Count <= actual.Count
            && expectedPrefix.Select((item, index) => IsWaitEvidenceSuccessor(item, actual[index])).All(value => value);

    private static bool HasHumanInputWaitingCheckpointPrefix(
        IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint>? expectedPrefix,
        IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint>? actual)
        => expectedPrefix is not null
            && actual is not null
            && expectedPrefix.Count <= actual.Count
            && expectedPrefix.Select((item, index) => item is not null
                && actual[index] is not null
                && string.Equals(item.CheckpointHash, actual[index].CheckpointHash, StringComparison.Ordinal)).All(value => value);

    private static bool IsWaitEvidenceSuccessor(
        GovernedLoopWaitExecutionEvidence? current,
        GovernedLoopWaitExecutionEvidence? candidate)
        => current is not null
            && candidate is not null
            && current.SchemaVersion == candidate.SchemaVersion
            && current.ActivationOrdinal == candidate.ActivationOrdinal
            && string.Equals(current.NodeId, candidate.NodeId, StringComparison.Ordinal)
            && current.NodeVisitOrdinal == candidate.NodeVisitOrdinal
            && string.Equals(current.CycleId, candidate.CycleId, StringComparison.Ordinal)
            && current.CycleIteration == candidate.CycleIteration
            && current.WaitAttempt == candidate.WaitAttempt
            && string.Equals(current.WaitOperationId, candidate.WaitOperationId, StringComparison.Ordinal)
            && string.Equals(current.Condition?.ContentHash, candidate.Condition?.ContentHash, StringComparison.Ordinal)
            && current.ParkedAtUtc == candidate.ParkedAtUtc
            && current.ParkedFrontierVersion == candidate.ParkedFrontierVersion
            && string.Equals(current.ParkedFrontierHash, candidate.ParkedFrontierHash, StringComparison.Ordinal)
            && (current.ParkEvidence is null
                || string.Equals(current.ParkEvidence.ContentHash, candidate.ParkEvidence?.ContentHash, StringComparison.Ordinal))
            && (current.ContinuationEvidence is null
                || string.Equals(current.ContinuationEvidence.ContentHash, candidate.ContinuationEvidence?.ContentHash, StringComparison.Ordinal));

    private static bool HasSameWaitIdentity(
        GovernedLoopWaitExecutionEvidence? current,
        GovernedLoopWaitExecutionEvidence? candidate)
        => current is not null
            && candidate is not null
            && current.SchemaVersion == candidate.SchemaVersion
            && current.ActivationOrdinal == candidate.ActivationOrdinal
            && string.Equals(current.NodeId, candidate.NodeId, StringComparison.Ordinal)
            && current.NodeVisitOrdinal == candidate.NodeVisitOrdinal
            && string.Equals(current.CycleId, candidate.CycleId, StringComparison.Ordinal)
            && current.CycleIteration == candidate.CycleIteration
            && current.WaitAttempt == candidate.WaitAttempt
            && string.Equals(current.WaitOperationId, candidate.WaitOperationId, StringComparison.Ordinal)
            && string.Equals(current.Condition?.ContentHash, candidate.Condition?.ContentHash, StringComparison.Ordinal)
            && current.ParkedAtUtc == candidate.ParkedAtUtc
            && current.ParkedFrontierVersion == candidate.ParkedFrontierVersion
            && string.Equals(current.ParkedFrontierHash, candidate.ParkedFrontierHash, StringComparison.Ordinal);

}
