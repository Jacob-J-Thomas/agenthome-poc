using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Application.Loops.Authoring;

public sealed class CustomLoopAuthoringService
{
    private readonly ICustomLoopDefinitionStore _store;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopIdentityGenerator _identityGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly ICustomLoopRunStore? _runStore;

    public CustomLoopAuthoringService(ICustomLoopDefinitionStore store, IAuditLog auditLog, ICustomLoopIdentityGenerator? identityGenerator = null, TimeProvider? timeProvider = null, ICustomLoopRunStore? runStore = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(auditLog);

        _store = store;
        _auditLog = auditLog;
        _identityGenerator = identityGenerator ?? new CustomLoopIdentityGenerator();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runStore = runStore;
    }

    public Task<IReadOnlyList<CustomLoopDefinition>> ListAsync(CancellationToken cancellationToken = default) => _store.ListAsync(cancellationToken);

    public Task<CustomLoopDefinition?> GetAsync(string loopId, CancellationToken cancellationToken = default) => _store.GetAsync(loopId, cancellationToken);

    public async Task<CustomLoopAuthoringResult> CreateAsync(string roleId, string operationId, string actor, CancellationToken cancellationToken = default)
    {
        var invalidOperation = ValidateOperationId(operationId);
        if (invalidOperation is not null)
        {
            return invalidOperation;
        }

        var requestHash = ComputeCreateRequestHash(roleId);
        var operation = await _store.GetMutationOperationAsync(operationId, cancellationToken);
        if (operation.Status is CustomLoopDefinitionMutationLookupStatus.PendingMutation or CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted)
        {
            var existing = operation.Operation ?? throw new InvalidOperationException("A persisted definition mutation lookup did not contain its operation record.");
            if (!MutationRequestMatches(existing, CustomLoopDefinitionMutationKind.Create, requestHash, existing.LoopId, roleId, null))
            {
                await TryAuditRejectionAsync("create", existing.LoopId, existing.PlannedDefinition, actor, operationId, "operation_reuse_conflict", cancellationToken);
                return Result(CustomLoopAuthoringStatus.Conflict, existing.ResultDefinition, "The mutation operation id was reused for a different authorized request.");
            }

            return await ReplayOrRecoverAsync(existing, actor, cancellationToken);
        }

        if (operation.Status != CustomLoopDefinitionMutationLookupStatus.NotFound)
        {
            throw new InvalidOperationException($"Unsupported custom-loop Create operation lookup status `{operation.Status}`.");
        }

        var legacyOperation = await _store.GetCreateOperationAsync(operationId, cancellationToken);
        if (legacyOperation.Status is CustomLoopCreateOperationLookupStatus.PendingDefinitionCommit or CustomLoopCreateOperationLookupStatus.Committed)
        {
            var existing = legacyOperation.Definition ?? throw new InvalidOperationException("A persisted Create operation did not contain its canonical definition snapshot.");
            if (!string.Equals(existing.RoleId, roleId, StringComparison.Ordinal))
            {
                await TryAuditRejectionAsync("create", existing.Id, existing, actor, operationId, "role_binding_mismatch", cancellationToken);
                return CustomLoopAuthoringResult.Invalid([new CustomLoopValidationError("role_binding_mismatch", "roleId", "The idempotent Create operation belongs to a different directory role.")]);
            }

            if (legacyOperation.Status == CustomLoopCreateOperationLookupStatus.Committed && legacyOperation.OperationIntegrity == CustomLoopOperationIntegrity.Complete)
            {
                return Result(CustomLoopAuthoringStatus.Replayed, existing, "The idempotent Create operation was already committed.");
            }

            var recovered = legacyOperation.Status == CustomLoopCreateOperationLookupStatus.PendingDefinitionCommit
                ? await _store.CreateAsync(existing, cancellationToken)
                : CustomLoopDefinitionStoreResult.AlreadyCreated(existing, legacyOperation.OperationIntegrity);
            return await CompleteMutationAsync("create", existing.Id, actor, operationId, recovered, existing, null, isReplay: legacyOperation.Status == CustomLoopCreateOperationLookupStatus.Committed);
        }

        if (legacyOperation.Status != CustomLoopCreateOperationLookupStatus.NotFound)
        {
            throw new InvalidOperationException($"Unsupported legacy custom-loop Create operation lookup status `{legacyOperation.Status}`.");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var definition = CustomLoopDefinition.CreateSeed(_identityGenerator.NewLoopId(), roleId, _identityGenerator.NewInferenceStepId(), operationId, now);
        var validation = CustomLoopDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            await TryAuditRejectionAsync("create", definition.Id, definition, actor, operationId, "validation_rejected", cancellationToken);
            return CustomLoopAuthoringResult.Invalid(validation.Errors);
        }

        if (!await TryAuditIntentAsync("create", definition.Id, definition, null, actor, operationId, cancellationToken))
        {
            return CustomLoopAuthoringResult.AuditUnavailable();
        }

        var mutation = new CustomLoopDefinitionMutationRequest(CustomLoopDefinitionMutationKind.Create, operationId, requestHash, definition.Id, roleId, null, definition, null, now);
        var storeResult = await _store.CreateAsync(definition, mutation, cancellationToken);
        return await CompleteMutationAsync("create", definition.Id, actor, operationId, storeResult, definition, null, isReplay: false);
    }

    public Task<CustomLoopAuthoringResult> UpdateAsync(string loopId, int expectedDefinitionVersion, string roleId, string operationId, string actor, CustomLoopDefinitionInput input, CancellationToken cancellationToken = default)
    {
        return UpdateAsync(loopId, expectedDefinitionVersion, roleId, operationId, actor, input, [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search], cancellationToken);
    }

    public async Task<CustomLoopAuthoringResult> UpdateAsync(
        string loopId,
        int expectedDefinitionVersion,
        string roleId,
        string operationId,
        string actor,
        CustomLoopDefinitionInput input,
        IReadOnlyCollection<CustomLoopToolAssignment> currentRoleCeiling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(currentRoleCeiling);
        if (currentRoleCeiling.Any(value => !Enum.IsDefined(value) || value == CustomLoopToolAssignment.Unknown) || currentRoleCeiling.Distinct().Count() != currentRoleCeiling.Count)
        {
            throw new ArgumentException("The server-owned current role ceiling must contain unique implemented assignments.", nameof(currentRoleCeiling));
        }

        input = NormalizeInput(input);

        var invalidOperation = ValidateOperationId(operationId);
        if (invalidOperation is not null)
        {
            return invalidOperation;
        }

        var requestHash = ComputeRequestHash(CustomLoopDefinitionMutationKind.Update, loopId, roleId, expectedDefinitionVersion, input);
        var operation = await _store.GetMutationOperationAsync(operationId, cancellationToken);
        if (operation.Status is CustomLoopDefinitionMutationLookupStatus.PendingMutation or CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted)
        {
            var existing = operation.Operation ?? throw new InvalidOperationException("A persisted definition mutation lookup did not contain its operation record.");
            if (!MutationRequestMatches(existing, CustomLoopDefinitionMutationKind.Update, requestHash, loopId, roleId, expectedDefinitionVersion))
            {
                await TryAuditRejectionAsync("update", loopId, existing.ResultDefinition ?? existing.PlannedDefinition, actor, operationId, "operation_reuse_conflict", cancellationToken);
                return Result(CustomLoopAuthoringStatus.Conflict, existing.ResultDefinition, "The mutation operation id was reused for a different authorized request.");
            }

            return await ReplayOrRecoverAsync(existing, actor, cancellationToken);
        }

        if (operation.Status != CustomLoopDefinitionMutationLookupStatus.NotFound)
        {
            throw new InvalidOperationException($"Unsupported custom-loop Update operation lookup status `{operation.Status}`.");
        }

        var current = await _store.GetAsync(loopId, cancellationToken);
        if (current is null)
        {
            await TryAuditRejectionAsync("update", loopId, null, actor, operationId, "not_found", cancellationToken);
            return Result(CustomLoopAuthoringStatus.NotFound, null, "The loop definition does not exist.");
        }

        if (!string.Equals(current.RoleId, roleId, StringComparison.Ordinal))
        {
            await TryAuditRejectionAsync("update", loopId, current, actor, operationId, "role_binding_mismatch", cancellationToken);
            return CustomLoopAuthoringResult.Invalid([new CustomLoopValidationError("role_binding_mismatch", "roleId", "The loop belongs to a different directory role.")]);
        }

        if (current.ContextDefaults != CustomLoopContextDefaults.CreatePrototypeDefaults())
        {
            await TryAuditRejectionAsync("update", loopId, current, actor, operationId, "server_context_defaults_changed", cancellationToken);
            return CustomLoopAuthoringResult.Invalid([new CustomLoopValidationError("server_context_defaults_changed", "contextDefaults", "The persisted server-owned context defaults do not match the supported schema and require review.")]);
        }

        if (await HasNonterminalRunAsync(loopId, cancellationToken))
        {
            await TryAuditRejectionAsync("update", loopId, current, actor, operationId, "active_run_exists", cancellationToken);
            return CustomLoopAuthoringResult.ActiveRun(current);
        }

        if (string.Equals(current.LastMutationOperationId, operationId, StringComparison.Ordinal))
        {
            if (MatchesInput(current, input))
            {
                return Result(CustomLoopAuthoringStatus.Replayed, current, "The idempotent Update operation was already committed.");
            }

            await TryAuditRejectionAsync("update", loopId, current, actor, operationId, "operation_reuse_conflict", cancellationToken);
            return Result(CustomLoopAuthoringStatus.Conflict, current, "The mutation operation id was reused with different content.");
        }

        var stepResult = BuildSteps(current, input.InferenceSteps);
        if (stepResult.Errors.Count > 0)
        {
            await TryAuditRejectionAsync("update", loopId, current, actor, operationId, "validation_rejected", cancellationToken);
            return CustomLoopAuthoringResult.Invalid(stepResult.Errors);
        }

        if (input.ToolAssignments is not null)
        {
            var unsupportedAssignments = input.ToolAssignments
                .Select((assignment, index) => (assignment, index))
                .Where(item => !Enum.IsDefined(item.assignment) || item.assignment == CustomLoopToolAssignment.Unknown)
                .Select(item => new CustomLoopValidationError("unsupported_tool_assignment", $"toolAssignments[{item.index}]", "Only list, read, and search may be assigned."))
                .ToArray();
            if (unsupportedAssignments.Length > 0)
            {
                await TryAuditRejectionAsync("update", loopId, current, actor, operationId, "validation_rejected", cancellationToken);
                return CustomLoopAuthoringResult.Invalid(unsupportedAssignments);
            }

            var outsideCurrentCeiling = input.ToolAssignments
                .Select((assignment, index) => (assignment, index))
                .Where(item => !currentRoleCeiling.Contains(item.assignment))
                .Select(item => new CustomLoopValidationError("tool_assignment_outside_role_ceiling", $"toolAssignments[{item.index}]", "The assignment is outside the current server-owned directory-role command ceiling."))
                .ToArray();
            if (outsideCurrentCeiling.Length > 0)
            {
                await TryAuditRejectionAsync("update", loopId, current, actor, operationId, "authority_ceiling_rejected", cancellationToken);
                return CustomLoopAuthoringResult.Invalid(outsideCurrentCeiling);
            }
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var definition = current with
        {
            DefinitionVersion = expectedDefinitionVersion + 1,
            ContentHash = new string('0', CustomLoopLimits.Sha256HexCharacters),
            UpdatedAtUtc = now,
            DisplayName = input.DisplayName,
            Description = input.Description,
            TriggerPolicy = input.TriggerPolicy,
            InferenceSteps = stepResult.Steps,
            ToolAssignments = input.ToolAssignments!,
            ExitPolicy = input.ExitPolicy,
            LastMutationOperationId = operationId
        };
        definition = CustomLoopDefinitionContentHash.Apply(definition);

        var validation = CustomLoopDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            await TryAuditRejectionAsync("update", loopId, definition, actor, operationId, "validation_rejected", cancellationToken);
            return CustomLoopAuthoringResult.Invalid(validation.Errors);
        }

        if (!await TryAuditIntentAsync("update", definition.Id, definition, current, actor, operationId, cancellationToken))
        {
            return CustomLoopAuthoringResult.AuditUnavailable();
        }

        var mutation = new CustomLoopDefinitionMutationRequest(CustomLoopDefinitionMutationKind.Update, operationId, requestHash, definition.Id, roleId, expectedDefinitionVersion, definition, current, now);
        var storeResult = await _store.UpdateAsync(definition, expectedDefinitionVersion, mutation, cancellationToken);
        return await CompleteMutationAsync("update", definition.Id, actor, operationId, storeResult, definition, current, isReplay: false);
    }

    public async Task<CustomLoopAuthoringResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string operationId, string actor, CancellationToken cancellationToken = default)
    {
        var invalidOperation = ValidateOperationId(operationId);
        if (invalidOperation is not null)
        {
            return invalidOperation;
        }

        var operation = await _store.GetMutationOperationAsync(operationId, cancellationToken);
        if (operation.Status is CustomLoopDefinitionMutationLookupStatus.PendingMutation or CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted)
        {
            var existing = operation.Operation ?? throw new InvalidOperationException("A persisted definition mutation lookup did not contain its operation record.");
            var existingRequestHash = ComputeRequestHash(CustomLoopDefinitionMutationKind.Delete, loopId, existing.RoleId, expectedDefinitionVersion, null);
            if (!MutationRequestMatches(existing, CustomLoopDefinitionMutationKind.Delete, existingRequestHash, loopId, existing.RoleId, expectedDefinitionVersion))
            {
                await TryAuditRejectionAsync("delete", loopId, existing.ResultDefinition ?? existing.PriorDefinition, actor, operationId, "operation_reuse_conflict", cancellationToken);
                return Result(CustomLoopAuthoringStatus.Conflict, existing.ResultDefinition, "The mutation operation id was reused for a different authorized request.");
            }

            return await ReplayOrRecoverAsync(existing, actor, cancellationToken);
        }

        if (operation.Status != CustomLoopDefinitionMutationLookupStatus.NotFound)
        {
            throw new InvalidOperationException($"Unsupported custom-loop Delete operation lookup status `{operation.Status}`.");
        }

        var current = await _store.GetAsync(loopId, cancellationToken);
        if (await HasNonterminalRunAsync(loopId, cancellationToken))
        {
            await TryAuditRejectionAsync("delete", loopId, current, actor, operationId, "active_run_exists", cancellationToken);
            return CustomLoopAuthoringResult.ActiveRun(current);
        }

        if (!await TryAuditIntentAsync("delete", loopId, current, current, actor, operationId, cancellationToken))
        {
            return CustomLoopAuthoringResult.AuditUnavailable();
        }

        var deletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        if (current is null)
        {
            var missing = await _store.DeleteAsync(loopId, expectedDefinitionVersion, operationId, deletedAtUtc, cancellationToken);
            return await CompleteMutationAsync("delete", loopId, actor, operationId, missing, null, null, isReplay: false);
        }

        var requestHash = ComputeRequestHash(CustomLoopDefinitionMutationKind.Delete, loopId, current.RoleId, expectedDefinitionVersion, null);
        var mutation = new CustomLoopDefinitionMutationRequest(CustomLoopDefinitionMutationKind.Delete, operationId, requestHash, loopId, current.RoleId, expectedDefinitionVersion, null, current, deletedAtUtc);
        var storeResult = await _store.DeleteAsync(loopId, expectedDefinitionVersion, operationId, deletedAtUtc, mutation, cancellationToken);
        return await CompleteMutationAsync("delete", loopId, actor, operationId, storeResult, current, current, isReplay: false);
    }

    private async Task<CustomLoopAuthoringResult> ReplayOrRecoverAsync(CustomLoopDefinitionMutationOperation operation, string actor, CancellationToken cancellationToken)
    {
        var operationName = operation.Kind.ToString().ToLowerInvariant();
        CustomLoopDefinitionStoreResult storeResult;
        if (operation.State == CustomLoopDefinitionMutationState.OutcomeCommitted)
        {
            storeResult = operation.ToStoreResult();
        }
        else
        {
            var mutation = new CustomLoopDefinitionMutationRequest(
                operation.Kind,
                operation.OperationId,
                operation.RequestHash,
                operation.LoopId,
                operation.RoleId,
                operation.ExpectedDefinitionVersion,
                operation.PlannedDefinition,
                operation.PriorDefinition,
                operation.RequestedAtUtc);
            storeResult = operation.Kind switch
            {
                CustomLoopDefinitionMutationKind.Create => await _store.CreateAsync(operation.PlannedDefinition ?? throw new InvalidOperationException("A pending Create operation is missing its planned definition."), mutation, cancellationToken),
                CustomLoopDefinitionMutationKind.Update => await _store.UpdateAsync(operation.PlannedDefinition ?? throw new InvalidOperationException("A pending Update operation is missing its planned definition."), operation.ExpectedDefinitionVersion ?? throw new InvalidOperationException("A pending Update operation is missing its expected version."), mutation, cancellationToken),
                CustomLoopDefinitionMutationKind.Delete => await _store.DeleteAsync(operation.LoopId, operation.ExpectedDefinitionVersion ?? throw new InvalidOperationException("A pending Delete operation is missing its expected version."), operation.OperationId, operation.UpdatedAtUtc, mutation, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported custom-loop definition mutation kind `{operation.Kind}`.")
            };
        }

        return await CompleteMutationAsync(operationName, operation.LoopId, actor, operation.OperationId, storeResult, operation.PlannedDefinition ?? operation.PriorDefinition, operation.PriorDefinition, isReplay: true);
    }

    private static bool MutationRequestMatches(CustomLoopDefinitionMutationOperation operation, CustomLoopDefinitionMutationKind kind, string requestHash, string loopId, string roleId, int? expectedDefinitionVersion)
    {
        return operation.Kind == kind
            && string.Equals(operation.RequestHash, requestHash, StringComparison.Ordinal)
            && string.Equals(operation.RoleId, roleId, StringComparison.Ordinal)
            && operation.ExpectedDefinitionVersion == expectedDefinitionVersion
            && (kind == CustomLoopDefinitionMutationKind.Create || string.Equals(operation.LoopId, loopId, StringComparison.Ordinal));
    }

    private static CustomLoopAuthoringResult? ValidateOperationId(string operationId)
    {
        return CustomLoopArtifactIdentifier.IsValid(operationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            ? null
            : CustomLoopAuthoringResult.Invalid([new CustomLoopValidationError("invalid_mutation_operation_id", "operationId", "Mutation operation id must be a bounded safe identifier.")]);
    }

    private static string ComputeCreateRequestHash(string roleId)
    {
        var canonicalRequest = "custom-loop-create\0" + roleId.Normalize(NormalizationForm.FormC);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
    }

    private static string ComputeRequestHash(CustomLoopDefinitionMutationKind kind, string loopId, string roleId, int expectedDefinitionVersion, CustomLoopDefinitionInput? input)
    {
        var request = new CanonicalMutationRequest(1, kind, loopId.Normalize(NormalizationForm.FormC), roleId.Normalize(NormalizationForm.FormC), expectedDefinitionVersion, input);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request))).ToLowerInvariant();
    }

    private async Task<bool> HasNonterminalRunAsync(string loopId, CancellationToken cancellationToken)
    {
        return _runStore is not null && await _runStore.GetNonterminalByLoopAsync(loopId, cancellationToken) is not null;
    }

    private (CustomLoopInferenceStep[] Steps, IReadOnlyList<CustomLoopValidationError> Errors) BuildSteps(CustomLoopDefinition current, CustomLoopInferenceStepInput[]? inputs)
    {
        if (inputs is null)
        {
            return ([], [new CustomLoopValidationError("inference_steps_required", "inferenceSteps", "Inference step list is required.")]);
        }

        var currentIds = current.InferenceSteps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<CustomLoopInferenceStep>(inputs.Length);
        var errors = new List<CustomLoopValidationError>();
        for (var index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            if (input is null)
            {
                errors.Add(new CustomLoopValidationError("inference_step_required", $"inferenceSteps[{index}]", "Inference step cannot be null."));
                continue;
            }

            var id = string.IsNullOrWhiteSpace(input.Id) ? _identityGenerator.NewInferenceStepId() : input.Id;
            if (!string.IsNullOrWhiteSpace(input.Id) && !currentIds.Contains(id))
            {
                errors.Add(new CustomLoopValidationError("unknown_inference_step_id", $"inferenceSteps[{index}].id", "An existing step id is immutable; omit the id for a new step."));
                continue;
            }

            if (!usedIds.Add(id))
            {
                errors.Add(new CustomLoopValidationError("duplicate_inference_step_id", $"inferenceSteps[{index}].id", "Inference step ids must be unique."));
                continue;
            }

            steps.Add(new CustomLoopInferenceStep(id, input.Name, input.Instruction, input.ContextPolicy));
        }

        return (steps.ToArray(), errors);
    }

    private static bool MatchesInput(CustomLoopDefinition definition, CustomLoopDefinitionInput input)
    {
        if (input.InferenceSteps is null
            || input.ToolAssignments is null
            || !string.Equals(definition.DisplayName, input.DisplayName, StringComparison.Ordinal)
            || !string.Equals(definition.Description, input.Description, StringComparison.Ordinal)
            || definition.TriggerPolicy != input.TriggerPolicy
            || definition.ExitPolicy != input.ExitPolicy
            || !definition.ToolAssignments.SequenceEqual(input.ToolAssignments)
            || definition.InferenceSteps.Length != input.InferenceSteps.Length)
        {
            return false;
        }

        for (var index = 0; index < definition.InferenceSteps.Length; index++)
        {
            var existing = definition.InferenceSteps[index];
            var requested = input.InferenceSteps[index];
            if (requested is null
                || !string.IsNullOrWhiteSpace(requested.Id) && !string.Equals(existing.Id, requested.Id, StringComparison.Ordinal)
                || !string.Equals(existing.Name, requested.Name, StringComparison.Ordinal)
                || !string.Equals(existing.Instruction, requested.Instruction, StringComparison.Ordinal)
                || existing.ContextPolicy != requested.ContextPolicy)
            {
                return false;
            }
        }

        return true;
    }

    private static CustomLoopDefinitionInput NormalizeInput(CustomLoopDefinitionInput input)
    {
        var trigger = input.TriggerPolicy is null
            ? null!
            : input.TriggerPolicy with { PresetPrompt = Normalize(input.TriggerPolicy.PresetPrompt) };
        var steps = input.InferenceSteps?.Select(step => step is null
            ? null!
            : step with { Name = Normalize(step.Name), Instruction = Normalize(step.Instruction) }).ToArray()!;
        var exit = input.ExitPolicy is null
            ? null!
            : input.ExitPolicy with { DecisionInstruction = Normalize(input.ExitPolicy.DecisionInstruction) };
        return input with
        {
            DisplayName = Normalize(input.DisplayName),
            Description = Normalize(input.Description),
            TriggerPolicy = trigger,
            InferenceSteps = steps,
            ExitPolicy = exit
        };
    }

    private static string Normalize(string? value)
    {
        return value?.Normalize(NormalizationForm.FormC) ?? null!;
    }

    private async Task<bool> TryAuditIntentAsync(string operation, string loopId, CustomLoopDefinition? definition, CustomLoopDefinition? priorDefinition, string actor, string operationId, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLog.AppendAsync(CreateMutationAudit(AuditSchema.Actions.LoopDefinitionMutationIntent, AuditSchema.Outcomes.Requested, operation, loopId, definition, priorDefinition, actor, operationId), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task TryAuditRejectionAsync(string operation, string loopId, CustomLoopDefinition? definition, string actor, string operationId, string rejectionCode, CancellationToken cancellationToken)
    {
        try
        {
            var auditEvent = CreateMutationAudit(AuditSchema.Actions.LoopDefinitionMutationOutcome, AuditSchema.Outcomes.Rejected, operation, loopId, definition, null, actor, operationId);
            var metadata = auditEvent.Metadata.ToDictionary(item => item.Key, item => item.Value);
            metadata["rejection_code"] = rejectionCode;
            await _auditLog.AppendAsync(auditEvent with { Metadata = metadata, Detail = $"Custom loop definition {operation} was rejected." }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private async Task<CustomLoopAuthoringResult> CompleteMutationAsync(string operation, string loopId, string actor, string operationId, CustomLoopDefinitionStoreResult storeResult, CustomLoopDefinition? fallbackDefinition, CustomLoopDefinition? priorDefinition, bool isReplay)
    {
        var result = MapStoreResult(storeResult, isReplay);
        if (storeResult.OperationIntegrity == CustomLoopOperationIntegrity.Complete)
        {
            return result;
        }

        var definition = storeResult.Definition ?? fallbackDefinition;
        using var integrityWindow = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _auditLog.AppendAsync(CreateMutationAudit(AuditSchema.Actions.LoopDefinitionMutationOutcome, AuditOutcome(storeResult.Status), operation, loopId, definition, priorDefinition, actor, operationId), integrityWindow.Token);
        }
        catch (Exception)
        {
            return result.IsCommitted
                ? result with { Status = CustomLoopAuthoringStatus.CommittedWithAuditWarning, Detail = "The definition mutation committed, but its outcome audit could not be recorded." }
                : result;
        }

        if (storeResult.OperationIntegrity != CustomLoopOperationIntegrity.PendingOutcomeAudit)
        {
            return result;
        }

        try
        {
            var markStatus = await _store.MarkOperationOutcomeAuditedAsync(operationId, integrityWindow.Token);
            return markStatus is CustomLoopOperationAuditMarkStatus.Marked or CustomLoopOperationAuditMarkStatus.AlreadyMarked
                ? result
                : result with { Status = CustomLoopAuthoringStatus.CommittedWithAuditWarning, Detail = "The definition mutation committed and its outcome audit was recorded, but the durable audit-integrity marker is incomplete." };
        }
        catch (Exception)
        {
            return result with { Status = CustomLoopAuthoringStatus.CommittedWithAuditWarning, Detail = "The definition mutation committed and its outcome audit was recorded, but the durable audit-integrity marker could not be recorded." };
        }
    }

    private static CustomLoopAuthoringResult MapStoreResult(CustomLoopDefinitionStoreResult result, bool isReplay = false)
    {
        if (isReplay && result.Status is CustomLoopDefinitionStoreStatus.Created or CustomLoopDefinitionStoreStatus.AlreadyCreated or CustomLoopDefinitionStoreStatus.Updated or CustomLoopDefinitionStoreStatus.Deleted or CustomLoopDefinitionStoreStatus.AlreadyDeleted)
        {
            return Result(CustomLoopAuthoringStatus.Replayed, result.Definition, "The idempotent definition mutation was already committed.");
        }

        return result.Status switch
        {
            CustomLoopDefinitionStoreStatus.Created => Result(CustomLoopAuthoringStatus.Created, result.Definition, "The loop definition was created."),
            CustomLoopDefinitionStoreStatus.AlreadyCreated => Result(CustomLoopAuthoringStatus.Replayed, result.Definition, "The idempotent Create operation was already committed."),
            CustomLoopDefinitionStoreStatus.Updated => Result(CustomLoopAuthoringStatus.Updated, result.Definition, "The loop definition was updated."),
            CustomLoopDefinitionStoreStatus.Deleted => Result(CustomLoopAuthoringStatus.Deleted, result.Definition, "The loop definition was deleted; historical runs remain available."),
            CustomLoopDefinitionStoreStatus.AlreadyDeleted => Result(CustomLoopAuthoringStatus.Replayed, result.Definition, "The idempotent Delete operation was already committed."),
            CustomLoopDefinitionStoreStatus.Conflict => new CustomLoopAuthoringResult(CustomLoopAuthoringStatus.Conflict, null, [], result.Conflict, "The loop definition changed; reload before saving."),
            CustomLoopDefinitionStoreStatus.OperationConflict => Result(CustomLoopAuthoringStatus.Conflict, null, "The mutation operation id was reused for a different authorized request."),
            CustomLoopDefinitionStoreStatus.NotFound => Result(CustomLoopAuthoringStatus.NotFound, null, "The loop definition does not exist."),
            CustomLoopDefinitionStoreStatus.LimitExceeded => Result(CustomLoopAuthoringStatus.LimitExceeded, null, "The workspace custom-loop definition limit was reached."),
            _ => throw new InvalidOperationException($"Unsupported custom-loop definition store status `{result.Status}`.")
        };
    }

    private static AuditEvent CreateMutationAudit(string action, string outcome, string operation, string loopId, CustomLoopDefinition? definition, CustomLoopDefinition? priorDefinition, string actor, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var metadata = new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["operation_id"] = operationId,
            ["loop_id"] = loopId,
            ["definition_version"] = definition?.DefinitionVersion,
            ["content_hash"] = definition?.ContentHash,
            ["role_id"] = definition?.RoleId ?? priorDefinition?.RoleId,
            ["tool_assignments"] = definition is null ? null : string.Join(',', definition.ToolAssignments.Select(assignment => assignment.ToString())),
            ["prior_tool_assignments"] = priorDefinition is null ? null : string.Join(',', priorDefinition.ToolAssignments.Select(assignment => assignment.ToString()))
        };
        return AuditEvent.Create(
            actor,
            action,
            loopId,
            outcome,
            $"Custom loop definition {operation} {outcome}.",
            metadata);
    }

    private static string AuditOutcome(CustomLoopDefinitionStoreStatus status)
    {
        return status switch
        {
            CustomLoopDefinitionStoreStatus.Created or CustomLoopDefinitionStoreStatus.AlreadyCreated or CustomLoopDefinitionStoreStatus.Updated or CustomLoopDefinitionStoreStatus.Deleted or CustomLoopDefinitionStoreStatus.AlreadyDeleted => AuditSchema.Outcomes.Succeeded,
            CustomLoopDefinitionStoreStatus.Conflict or CustomLoopDefinitionStoreStatus.OperationConflict => AuditSchema.Outcomes.Conflict,
            CustomLoopDefinitionStoreStatus.NotFound => AuditSchema.Outcomes.NotFound,
            CustomLoopDefinitionStoreStatus.LimitExceeded => AuditSchema.Outcomes.Denied,
            _ => AuditSchema.Outcomes.Unknown
        };
    }

    private static CustomLoopAuthoringResult Result(CustomLoopAuthoringStatus status, CustomLoopDefinition? definition, string detail)
    {
        return new CustomLoopAuthoringResult(status, definition, [], null, detail);
    }

    private sealed record CanonicalMutationRequest(
        int SchemaVersion,
        CustomLoopDefinitionMutationKind Kind,
        string LoopId,
        string RoleId,
        int ExpectedDefinitionVersion,
        CustomLoopDefinitionInput? Input);
}
