using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopAdmissionService
{
    private static readonly TimeSpan IntegrityWriteTimeout = TimeSpan.FromSeconds(30);
    private readonly ICustomLoopDefinitionStore _definitionStore;
    private readonly ICustomLoopRunStore _runStore;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopRunIdentityGenerator _identityGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;

    public CustomLoopAdmissionService(ICustomLoopDefinitionStore definitionStore, ICustomLoopRunStore runStore, IAuditLog auditLog, ICustomLoopToolAuthorityProvider authorityProvider, ICustomLoopRunIdentityGenerator? identityGenerator = null, TimeProvider? timeProvider = null)
    {
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _authorityProvider = authorityProvider ?? throw new ArgumentNullException(nameof(authorityProvider));
        _identityGenerator = identityGenerator ?? new CustomLoopRunIdentityGenerator();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CustomLoopAdmissionResult> AdmitAsync(CustomLoopAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = ValidateRequestEnvelope(request);
        if (errors.Count > 0)
        {
            return await AuditOutcomeAsync(request, CustomLoopAdmissionResult.Invalid(errors), useIntegrityWindow: false, cancellationToken);
        }

        var invocationPrompt = Normalize(request.InvocationPrompt);
        CustomLoopRunRecord? replay;
        try
        {
            replay = await _runStore.GetByAdmissionOperationAsync(request.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var result = Result(CustomLoopAdmissionStatus.Invalid, null, $"The invocation operation record could not be read safely: {exception.GetType().Name}.");
            return await AuditOutcomeAsync(request, result, useIntegrityWindow: false, cancellationToken);
        }

        if (replay is not null)
        {
            return await AuditOutcomeAsync(request, Replay(request, invocationPrompt, replay), useIntegrityWindow: true, cancellationToken);
        }

        CustomLoopDefinition? definition;
        try
        {
            definition = await _definitionStore.GetAsync(request.LoopId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var result = Result(CustomLoopAdmissionStatus.Invalid, null, $"The loop definition could not be read safely: {exception.GetType().Name}.");
            return await AuditOutcomeAsync(request, result, useIntegrityWindow: false, cancellationToken);
        }

        if (definition is null)
        {
            var result = Result(CustomLoopAdmissionStatus.NotFound, null, "The custom-loop definition does not exist.");
            return await AuditOutcomeAsync(request, result, useIntegrityWindow: false, cancellationToken);
        }

        CustomLoopToolAuthoritySnapshot authority;
        try
        {
            authority = await _authorityProvider.ResolveAsync(definition.RoleId, definition.ToolAssignments, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var result = Result(CustomLoopAdmissionStatus.Invalid, null, $"The current directory-role authority could not be resolved safely: {exception.GetType().Name}.");
            return await AuditOutcomeAsync(request, result, useIntegrityWindow: false, cancellationToken);
        }

        errors.AddRange(ValidateDefinitionBinding(request, definition, authority));
        var triggerPrompt = ResolveTriggerPrompt(definition, invocationPrompt, errors);
        ValidateCapturedContext(request, definition, errors);
        if (errors.Count > 0)
        {
            return await AuditOutcomeAsync(request, CustomLoopAdmissionResult.Invalid(errors), useIntegrityWindow: false, cancellationToken);
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var admittedEvent = new CustomLoopRunEvent(
            1,
            _identityGenerator.NewEventId(),
            now,
            CustomLoopRunEventKind.Admitted,
            null,
            null,
            null,
            "The canonical definition, trigger, model, role, and context snapshot were admitted before provider dispatch.",
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
            null,
            authority);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            _identityGenerator.NewRunId(),
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            now,
            now,
            null,
            request.Surface,
            request.ModelSnapshot,
            request.OperationId,
            new string('0', CustomLoopLimits.Sha256HexCharacters),
            definition,
            triggerPrompt,
            request.InvokingConversation,
            request.ContextSnapshot,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admittedEvent],
            null,
            null,
            null);
        run = CustomLoopAdmissionRequestHash.Apply(run);
        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            return await AuditOutcomeAsync(request, CustomLoopAdmissionResult.Invalid(validation.Errors), useIntegrityWindow: false, cancellationToken);
        }

        CustomLoopRunStoreResult stored;
        try
        {
            stored = await _runStore.CreateAsync(run, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var result = Result(CustomLoopAdmissionStatus.Invalid, null, $"The admitted run could not be persisted: {exception.GetType().Name}.");
            return await AuditOutcomeAsync(request, result, useIntegrityWindow: false, cancellationToken);
        }

        switch (stored.Status)
        {
            case CustomLoopRunStoreStatus.AlreadyCreated when stored.Run is not null:
                return await AuditOutcomeAsync(request, Replay(request, invocationPrompt, stored.Run), useIntegrityWindow: true, cancellationToken);

            case CustomLoopRunStoreStatus.OperationConflict:
                return await AuditOutcomeAsync(request, Result(CustomLoopAdmissionStatus.Conflict, stored.Run, "The invocation operation id was already used for a different canonical request."), useIntegrityWindow: true, cancellationToken);

            case CustomLoopRunStoreStatus.NonterminalRunExists:
                return await AuditOutcomeAsync(request, Result(CustomLoopAdmissionStatus.NonterminalRunExists, stored.Run, "This loop already has a nonterminal run; finish or cancel it before invoking again."), useIntegrityWindow: true, cancellationToken);

            case CustomLoopRunStoreStatus.LimitExceeded:
                return await AuditOutcomeAsync(request, Result(CustomLoopAdmissionStatus.LimitExceeded, null, "The workspace run-trace quota is full; no provider request was dispatched."), useIntegrityWindow: false, cancellationToken);

            case not CustomLoopRunStoreStatus.Created:
                return await AuditOutcomeAsync(request, Result(CustomLoopAdmissionStatus.Invalid, stored.Run, $"The run store rejected admission with status `{stored.Status}`."), useIntegrityWindow: stored.Run is not null, cancellationToken);
        }

        run = stored.Run ?? run;
        try
        {
            using (var auditIntegrityWindow = new CancellationTokenSource(IntegrityWriteTimeout))
            {
                await _auditLog.AppendAsync(CreateAdmissionAudit(request, Result(CustomLoopAdmissionStatus.Admitted, run, "The custom-loop run was admitted and is ready for ordered execution.")), auditIntegrityWindow.Token);
            }

            using var markerIntegrityWindow = new CancellationTokenSource(IntegrityWriteTimeout);
            run = await CompleteAdmissionAuditAsync(run, markerIntegrityWindow.Token);
            return Result(CustomLoopAdmissionStatus.Admitted, run, "The custom-loop run was admitted and its audit-integrity marker is durable before ordered execution.");
        }
        catch (Exception exception)
        {
            var failed = await FailAdmissionIntegrityAsync(run, exception);
            return new CustomLoopAdmissionResult(CustomLoopAdmissionStatus.AuditUnavailable, failed, [], "The run was persisted, but admission audit integrity could not be completed; no provider request was dispatched.");
        }
    }

    private static List<CustomLoopValidationError> ValidateRequestEnvelope(CustomLoopAdmissionRequest request)
    {
        var errors = new List<CustomLoopValidationError>();
        if (!CustomLoopArtifactIdentifier.IsValid(request.LoopId))
        {
            errors.Add(Error("invalid_loop_id", "loopId", "Loop id must be a safe server-issued artifact identifier."));
        }

        if (!CustomLoopArtifactIdentifier.IsValid(request.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters))
        {
            errors.Add(Error("invalid_operation_id", "operationId", "Operation id must be a bounded safe identifier."));
        }

        if (!CustomLoopArtifactIdentifier.IsValid(request.CurrentRoleId))
        {
            errors.Add(Error("invalid_current_role", "currentRoleId", "Current role id must be a safe server-resolved identifier."));
        }

        if (request.ExpectedDefinitionVersion < 1)
        {
            errors.Add(Error("invalid_expected_version", "expectedDefinitionVersion", "Expected definition version must be at least one."));
        }

        if (!IsHash(request.ExpectedDefinitionHash))
        {
            errors.Add(Error("invalid_expected_hash", "expectedDefinitionHash", "Expected definition hash must be lowercase SHA-256 hexadecimal."));
        }

        if (string.IsNullOrWhiteSpace(request.Actor))
        {
            errors.Add(Error("actor_required", "actor", "The authenticated server actor is required."));
        }

        if (!IsSurface(request.Surface))
        {
            errors.Add(Error("invalid_surface", "surface", "Surface must be a normalized server-owned identifier."));
        }

        if (request.ModelSnapshot is null || string.IsNullOrWhiteSpace(request.ModelSnapshot.Provider))
        {
            errors.Add(Error("model_snapshot_required", "modelSnapshot", "The server-resolved provider/model snapshot is required."));
        }

        if (request.ContextSnapshot is null)
        {
            errors.Add(Error("context_snapshot_required", "contextSnapshot", "The server-captured context snapshot is required."));
        }

        var prompt = Normalize(request.InvocationPrompt);
        if (prompt.Length > CustomLoopLimits.MaxPresetPromptCharacters)
        {
            errors.Add(Error("invocation_prompt_too_long", "invocationPrompt", $"Invocation prompt cannot exceed {CustomLoopLimits.MaxPresetPromptCharacters} characters."));
        }

        return errors;
    }

    private static IReadOnlyList<CustomLoopValidationError> ValidateDefinitionBinding(CustomLoopAdmissionRequest request, CustomLoopDefinition definition, CustomLoopToolAuthoritySnapshot authority)
    {
        var errors = new List<CustomLoopValidationError>();
        var validation = CustomLoopDefinitionValidator.Validate(definition);
        errors.AddRange(validation.Errors);
        if (!string.Equals(definition.RoleId, request.CurrentRoleId, StringComparison.Ordinal))
        {
            errors.Add(Error("role_binding_mismatch", "currentRoleId", "The loop is not bound to the current server-resolved directory role."));
        }

        if (!authority.IsValid || !string.Equals(authority.RoleId, request.CurrentRoleId, StringComparison.Ordinal))
        {
            errors.Add(Error("current_role_authority_invalid", "toolAssignments", "The current server-owned directory-role authority could not be validated."));
        }
        else if (authority.EffectiveAssignments.Length != definition.ToolAssignments.Length || !authority.EffectiveAssignments.OrderBy(value => value).SequenceEqual(definition.ToolAssignments.OrderBy(value => value)))
        {
            errors.Add(Error("tool_assignment_outside_role_ceiling", "toolAssignments", "One or more saved assignments are outside the current server-owned directory-role command ceiling."));
        }

        if (definition.ContextDefaults != CustomLoopContextDefaults.CreatePrototypeDefaults())
        {
            errors.Add(Error("server_context_defaults_changed", "contextDefaults", "The persisted server-owned context defaults require review."));
        }

        if (definition.DefinitionVersion != request.ExpectedDefinitionVersion || !string.Equals(definition.ContentHash, request.ExpectedDefinitionHash, StringComparison.Ordinal))
        {
            errors.Add(Error("definition_conflict", "expectedDefinitionVersion", "The loop changed; reload its current version and hash before invoking."));
        }

        return errors;
    }

    private static string ResolveTriggerPrompt(CustomLoopDefinition definition, string invocationPrompt, List<CustomLoopValidationError> errors)
    {
        switch (definition.TriggerPolicy.PromptSource)
        {
            case CustomLoopTriggerPromptSource.Invocation:
                if (string.IsNullOrWhiteSpace(invocationPrompt))
                {
                    errors.Add(Error("invocation_prompt_required", "invocationPrompt", "This loop requires an invoking user prompt."));
                }

                return invocationPrompt;

            case CustomLoopTriggerPromptSource.Preset:
                if (!string.IsNullOrWhiteSpace(invocationPrompt))
                {
                    errors.Add(Error("invocation_prompt_not_allowed", "invocationPrompt", "A preset-trigger loop does not accept an invocation prompt."));
                }

                return definition.TriggerPolicy.PresetPrompt;

            case CustomLoopTriggerPromptSource.None:
                if (!string.IsNullOrWhiteSpace(invocationPrompt))
                {
                    errors.Add(Error("invocation_prompt_not_allowed", "invocationPrompt", "A no-prompt loop does not accept an invocation prompt."));
                }

                return string.Empty;

            default:
                errors.Add(Error("unsupported_trigger_source", "triggerPolicy.promptSource", "The trigger prompt source is unsupported."));
                return string.Empty;
        }
    }

    private static void ValidateCapturedContext(CustomLoopAdmissionRequest request, CustomLoopDefinition definition, List<CustomLoopValidationError> errors)
    {
        if (!CustomLoopContextSnapshotHash.Matches(request.ContextSnapshot))
        {
            errors.Add(Error("context_manifest_mismatch", "contextSnapshot.manifestHash", "The server-captured context manifest hash does not match its exact content."));
        }

        if (!definition.TriggerPolicy.IncludeInvokingConversation && request.ContextSnapshot.InvokingConversationMessages.Length > 0)
        {
            errors.Add(Error("conversation_not_admitted", "contextSnapshot.invokingConversationMessages", "Trigger excluded invoking-conversation history, so its snapshot must be empty."));
        }

        if (request.InvokingConversation is null && request.ContextSnapshot.InvokingConversationMessages.Length > 0)
        {
            errors.Add(Error("conversation_binding_required", "invokingConversation", "Conversation history cannot be captured without a server-owned conversation binding."));
        }
    }

    private static CustomLoopAdmissionResult Replay(CustomLoopAdmissionRequest request, string invocationPrompt, CustomLoopRunRecord run)
    {
        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            return CustomLoopAdmissionResult.Invalid(validation.Errors);
        }

        var triggerErrors = new List<CustomLoopValidationError>();
        var resolvedPrompt = ResolveTriggerPrompt(run.AdmittedDefinition, invocationPrompt, triggerErrors);
        var matches = triggerErrors.Count == 0
            && string.Equals(run.AdmissionOperationId, request.OperationId, StringComparison.Ordinal)
            && string.Equals(run.LoopId, request.LoopId, StringComparison.Ordinal)
            && run.AdmittedDefinition.DefinitionVersion == request.ExpectedDefinitionVersion
            && string.Equals(run.AdmittedDefinition.ContentHash, request.ExpectedDefinitionHash, StringComparison.Ordinal)
            && string.Equals(run.AdmittedDefinition.RoleId, request.CurrentRoleId, StringComparison.Ordinal)
            && string.Equals(run.Surface, request.Surface, StringComparison.Ordinal)
            && string.Equals(run.TriggerPrompt, resolvedPrompt, StringComparison.Ordinal)
            && ModelSnapshotsEqual(run.ModelSnapshot, request.ModelSnapshot)
            && ConversationReferencesEqual(run.InvokingConversation, request.InvokingConversation)
            && ContextSnapshotsEqual(run.ContextSnapshot, request.ContextSnapshot);
        if (!matches)
        {
            return Result(CustomLoopAdmissionStatus.Conflict, run, "The invocation operation id was reused with different authorized request content.");
        }

        return CustomLoopRunValidator.HasCompleteAdmissionAudit(run)
            ? Result(CustomLoopAdmissionStatus.Replayed, run, "The invocation operation was already admitted; its original run was returned.")
            : Result(CustomLoopAdmissionStatus.AuditUnavailable, run, "The original invocation has no durable admission-audit completion marker and cannot be replayed as admitted.");
    }

    private static bool ModelSnapshotsEqual(CustomLoopModelSnapshot? left, CustomLoopModelSnapshot? right)
    {
        return ReferenceEquals(left, right)
            || left is not null && right is not null
            && string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
            && string.Equals(left.Model, right.Model, StringComparison.Ordinal);
    }

    private static bool ConversationReferencesEqual(CustomLoopConversationReference? left, CustomLoopConversationReference? right)
    {
        return ReferenceEquals(left, right)
            || left is not null && right is not null
            && string.Equals(left.ConversationId, right.ConversationId, StringComparison.Ordinal)
            && string.Equals(left.CapturedVersion, right.CapturedVersion, StringComparison.Ordinal)
            && left.CapturedAtUtc == right.CapturedAtUtc;
    }

    private static bool ContextSnapshotsEqual(CustomLoopContextSnapshot? left, CustomLoopContextSnapshot? right)
    {
        return ReferenceEquals(left, right)
            || left is not null && right is not null
            && left.CapturedAtUtc == right.CapturedAtUtc
            && string.Equals(left.ManifestHash, right.ManifestHash, StringComparison.Ordinal)
            && MessageSnapshotsEqual(left.DirectoryRoleMessages, right.DirectoryRoleMessages)
            && MessageSnapshotsEqual(left.InvokingConversationMessages, right.InvokingConversationMessages);
    }

    private static bool MessageSnapshotsEqual(CustomLoopMessageSnapshot[]? left, CustomLoopMessageSnapshot[]? right)
    {
        return ReferenceEquals(left, right)
            || left is not null && right is not null && left.SequenceEqual(right);
    }

    private async Task<CustomLoopAdmissionResult> AuditOutcomeAsync(CustomLoopAdmissionRequest request, CustomLoopAdmissionResult result, bool useIntegrityWindow, CancellationToken cancellationToken)
    {
        using var integrityWindow = useIntegrityWindow ? new CancellationTokenSource(IntegrityWriteTimeout) : null;
        var auditToken = integrityWindow?.Token ?? cancellationToken;
        try
        {
            await _auditLog.AppendAsync(CreateAdmissionAudit(request, result), auditToken);
            return result;
        }
        catch (OperationCanceledException) when (!useIntegrityWindow && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new CustomLoopAdmissionResult(CustomLoopAdmissionStatus.AuditUnavailable, result.Run, [], "The admission outcome could not be audited; no provider request was dispatched.");
        }
    }

    private async Task<CustomLoopRunRecord> CompleteAdmissionAuditAsync(CustomLoopRunRecord run, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (now < run.UpdatedAtUtc)
        {
            now = run.UpdatedAtUtc;
        }

        var marker = new CustomLoopRunEvent(run.Events.Length + 1, _identityGenerator.NewEventId(), now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "The matching admission outcome audit is durable; provider dispatch may now be considered.", [], null, null, null, null, null, null, null, null, null, null);
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            UpdatedAtUtc = now,
            Events = [.. run.Events, marker]
        };
        var validation = CustomLoopRunValidator.ValidateUpdate(run, candidate);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("The admission-audit completion marker did not form a valid durable run successor.");
        }

        var stored = await _runStore.UpdateAsync(candidate, run.LifecycleVersion, cancellationToken);
        if (stored.Status != CustomLoopRunStoreStatus.Updated || stored.Run is null || !CustomLoopRunValidator.HasCompleteAdmissionAudit(stored.Run))
        {
            throw new InvalidOperationException($"The run store rejected the admission-audit completion marker with status `{stored.Status}`.");
        }

        return stored.Run;
    }

    private async Task<CustomLoopRunRecord> FailAdmissionIntegrityAsync(CustomLoopRunRecord run, Exception exception)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (now < run.UpdatedAtUtc)
        {
            now = run.UpdatedAtUtc;
        }

        var detail = $"Admission audit integrity failed before provider dispatch: {exception.GetType().Name}.";
        var lifecycle = new CustomLoopRunEvent(run.Events.Length + 1, _identityGenerator.NewEventId(), now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, detail, [], null, null, null, null, null, null, null, null, null, null);
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Failed,
            UpdatedAtUtc = now,
            CompletedAtUtc = now,
            Events = [.. run.Events, lifecycle],
            FailureCode = "admission_audit_failed",
            FailureDetail = detail
        };
        using var integrityWindow = new CancellationTokenSource(IntegrityWriteTimeout);
        try
        {
            var result = await _runStore.UpdateAsync(candidate, run.LifecycleVersion, integrityWindow.Token);
            return result.Run ?? run;
        }
        catch
        {
            return run;
        }
    }

    private static AuditEvent CreateAdmissionAudit(CustomLoopAdmissionRequest request, CustomLoopAdmissionResult result)
    {
        var run = result.Run;
        var admittedDefinition = run?.AdmittedDefinition;
        var hasEffectiveAdmission = result.Status is CustomLoopAdmissionStatus.Admitted or CustomLoopAdmissionStatus.Replayed;
        var metadata = new Dictionary<string, object?>
        {
            ["admission_status"] = ToAuditStatus(result.Status),
            ["run_id"] = SafeArtifactId(run?.Id),
            ["loop_id"] = SafeArtifactId(request.LoopId),
            ["operation_id"] = SafeArtifactId(request.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters),
            ["definition_version"] = request.ExpectedDefinitionVersion >= 1 ? request.ExpectedDefinitionVersion : null,
            ["definition_hash"] = IsHash(request.ExpectedDefinitionHash) ? request.ExpectedDefinitionHash : null,
            ["role_id"] = SafeArtifactId(request.CurrentRoleId),
            ["surface"] = IsSurface(request.Surface) ? request.Surface : null,
            ["effective_tool_assignments"] = hasEffectiveAdmission && admittedDefinition?.ToolAssignments is not null ? string.Join(',', admittedDefinition.ToolAssignments.Select(value => value.ToString())) : string.Empty,
            ["validation_codes"] = string.Join(',', result.ValidationErrors.Select(error => error.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        };
        var target = SafeArtifactId(run?.Id) ?? SafeArtifactId(request.LoopId) ?? "custom-loop-admission";
        return AuditEvent.Create(SafeAuditActor(request.Actor), AuditSchema.Actions.LoopRunAdmission, target, ToAuditOutcome(result.Status), "Custom-loop invocation admission outcome recorded.", metadata);
    }

    private static string SafeAuditActor(string? actor)
    {
        return !string.IsNullOrWhiteSpace(actor)
            && actor.Length <= CustomLoopLimits.MaxArtifactIdCharacters
            && actor.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '@' or ':')
                ? actor
                : "embodysense.unknown";
    }

    private static string? SafeArtifactId(string? value, int maxCharacters = CustomLoopLimits.MaxArtifactIdCharacters)
    {
        return CustomLoopArtifactIdentifier.IsValid(value, maxCharacters) ? value : null;
    }

    private static string ToAuditStatus(CustomLoopAdmissionStatus status)
    {
        return status switch
        {
            CustomLoopAdmissionStatus.Admitted => "admitted",
            CustomLoopAdmissionStatus.Replayed => "replayed",
            CustomLoopAdmissionStatus.Invalid => "invalid",
            CustomLoopAdmissionStatus.Conflict => "conflict",
            CustomLoopAdmissionStatus.NonterminalRunExists => "nonterminal_run_exists",
            CustomLoopAdmissionStatus.LimitExceeded => "limit_exceeded",
            CustomLoopAdmissionStatus.NotFound => "not_found",
            CustomLoopAdmissionStatus.AuditUnavailable => "audit_unavailable",
            _ => "unknown"
        };
    }

    private static string ToAuditOutcome(CustomLoopAdmissionStatus status)
    {
        return status switch
        {
            CustomLoopAdmissionStatus.Admitted or CustomLoopAdmissionStatus.Replayed => AuditSchema.Outcomes.Succeeded,
            CustomLoopAdmissionStatus.Conflict or CustomLoopAdmissionStatus.NonterminalRunExists => AuditSchema.Outcomes.Conflict,
            CustomLoopAdmissionStatus.NotFound => AuditSchema.Outcomes.NotFound,
            _ => AuditSchema.Outcomes.Rejected
        };
    }

    private static string Normalize(string? value) => value?.Normalize(NormalizationForm.FormC) ?? string.Empty;

    private static bool IsHash(string? value) => value is { Length: CustomLoopLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSurface(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static CustomLoopValidationError Error(string code, string field, string message) => new(code, field, message);

    private static CustomLoopAdmissionResult Result(CustomLoopAdmissionStatus status, CustomLoopRunRecord? run, string detail) => new(status, run, [], detail);

}
