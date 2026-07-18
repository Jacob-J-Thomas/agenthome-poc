namespace EmbodySense.Core.Common.Loops.Models.Custom;

public static class CustomLoopDefinitionValidator
{
    public static CustomLoopValidationResult Validate(CustomLoopDefinition? definition)
    {
        var errors = new List<CustomLoopValidationError>();
        if (definition is null)
        {
            Add(errors, "definition_required", "$", "Custom loop definition is required.");
            return new CustomLoopValidationResult(errors);
        }

        ValidateServerMetadata(definition, errors);
        ValidateText(definition.DisplayName, "display_name_required", "display_name_too_long", "displayName", CustomLoopLimits.MaxNameCharacters, required: true, errors);
        ValidateText(definition.Description, "description_required", "description_too_long", "description", CustomLoopLimits.MaxDescriptionCharacters, required: false, errors);
        ValidateTrigger(definition.TriggerPolicy, errors);
        ValidateContextDefaults(definition.ContextDefaults, errors);
        ValidateInferenceSteps(definition.InferenceSteps, errors);
        ValidateToolAssignments(definition.ToolAssignments, errors);
        ValidateExit(definition.ExitPolicy, errors);

        if (errors.Count == 0 && !CustomLoopDefinitionContentHash.Matches(definition))
        {
            Add(errors, "content_hash_mismatch", "contentHash", "Content hash does not match the canonical custom-loop definition.");
        }

        return new CustomLoopValidationResult(errors);
    }

    private static void ValidateServerMetadata(CustomLoopDefinition definition, List<CustomLoopValidationError> errors)
    {
        if (definition.SchemaVersion != CustomLoopDefinition.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_schema_version", "schemaVersion", $"Schema version must be {CustomLoopDefinition.CurrentSchemaVersion}.");
        }

        ValidateArtifactId(definition.Id, "id", errors);
        if (definition.DefinitionVersion < 1)
        {
            Add(errors, "invalid_definition_version", "definitionVersion", "Definition version must be at least 1.");
        }

        if (!IsSha256Hex(definition.ContentHash))
        {
            Add(errors, "invalid_content_hash", "contentHash", "Content hash must be a 64-character lowercase SHA-256 hexadecimal value.");
        }

        if (!IsUtcTimestamp(definition.CreatedAtUtc))
        {
            Add(errors, "invalid_created_timestamp", "createdAtUtc", "Created timestamp must be a non-default UTC value.");
        }

        if (!IsUtcTimestamp(definition.UpdatedAtUtc))
        {
            Add(errors, "invalid_updated_timestamp", "updatedAtUtc", "Updated timestamp must be a non-default UTC value.");
        }

        if (definition.CreatedAtUtc > definition.UpdatedAtUtc)
        {
            Add(errors, "invalid_timestamp_order", "updatedAtUtc", "Updated timestamp cannot precede the created timestamp.");
        }

        ValidateArtifactId(definition.RoleId, "roleId", errors);
        ValidateOperationId(definition.LastMutationOperationId, errors);
    }

    private static void ValidateTrigger(CustomLoopTriggerPolicy? trigger, List<CustomLoopValidationError> errors)
    {
        if (trigger is null)
        {
            Add(errors, "trigger_policy_required", "triggerPolicy", "Trigger policy is required.");
            return;
        }

        if (!Enum.IsDefined(trigger.PromptSource) || trigger.PromptSource == CustomLoopTriggerPromptSource.Unknown)
        {
            Add(errors, "unsupported_trigger_source", "triggerPolicy.promptSource", "Trigger prompt source must be invocation, preset, or none.");
        }

        ValidateText(trigger.PresetPrompt, "preset_prompt_required", "preset_prompt_too_long", "triggerPolicy.presetPrompt", CustomLoopLimits.MaxPresetPromptCharacters, required: trigger.PromptSource == CustomLoopTriggerPromptSource.Preset, errors);
        if (trigger.PromptSource != CustomLoopTriggerPromptSource.Preset && !string.IsNullOrEmpty(trigger.PresetPrompt))
        {
            Add(errors, "unexpected_preset_prompt", "triggerPolicy.presetPrompt", "Preset prompt must be empty unless preset is the selected trigger source.");
        }
    }

    private static void ValidateContextDefaults(CustomLoopContextDefaults? defaults, List<CustomLoopValidationError> errors)
    {
        if (defaults is null)
        {
            Add(errors, "context_defaults_required", "contextDefaults", "Typed loop context defaults are required.");
            return;
        }

        ValidateResolvedContextPolicy(defaults.Inference, "contextDefaults.inference", errors);
        ValidateResolvedContextPolicy(defaults.Exit, "contextDefaults.exit", errors);
    }

    private static void ValidateInferenceSteps(CustomLoopInferenceStep[]? steps, List<CustomLoopValidationError> errors)
    {
        if (steps is null)
        {
            Add(errors, "inference_steps_required", "inferenceSteps", "Inference step list is required.");
            return;
        }

        if (steps.Length < CustomLoopLimits.MinInferenceSteps || steps.Length > CustomLoopLimits.MaxInferenceSteps)
        {
            Add(errors, "inference_step_count_out_of_range", "inferenceSteps", $"Inference step count must be between {CustomLoopLimits.MinInferenceSteps} and {CustomLoopLimits.MaxInferenceSteps}.");
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < steps.Length; index++)
        {
            var field = $"inferenceSteps[{index}]";
            var step = steps[index];
            if (step is null)
            {
                Add(errors, "inference_step_required", field, "Inference step cannot be null.");
                continue;
            }

            ValidateArtifactId(step.Id, $"{field}.id", errors);
            if (!string.IsNullOrEmpty(step.Id) && !stepIds.Add(step.Id))
            {
                Add(errors, "duplicate_inference_step_id", $"{field}.id", "Inference step ids must be unique.");
            }

            ValidateText(step.Name, "inference_step_name_required", "inference_step_name_too_long", $"{field}.name", CustomLoopLimits.MaxNameCharacters, required: true, errors);
            ValidateText(step.Instruction, "inference_instruction_required", "inference_instruction_too_long", $"{field}.instruction", CustomLoopLimits.MaxInstructionCharacters, required: true, errors);
            ValidateNodeContextPolicy(step.ContextPolicy, $"{field}.contextPolicy", errors);
        }
    }

    private static void ValidateToolAssignments(CustomLoopToolAssignment[]? assignments, List<CustomLoopValidationError> errors)
    {
        if (assignments is null)
        {
            Add(errors, "tool_assignments_required", "toolAssignments", "Tool assignment list is required, even when empty.");
            return;
        }

        var assigned = new HashSet<CustomLoopToolAssignment>();
        for (var index = 0; index < assignments.Length; index++)
        {
            var assignment = assignments[index];
            if (!Enum.IsDefined(assignment) || assignment == CustomLoopToolAssignment.Unknown)
            {
                Add(errors, "unsupported_tool_assignment", $"toolAssignments[{index}]", "Only list, read, and search may be assigned.");
                continue;
            }

            if (!assigned.Add(assignment))
            {
                Add(errors, "duplicate_tool_assignment", $"toolAssignments[{index}]", "Tool assignments must be unique.");
            }
        }
    }

    private static void ValidateExit(CustomLoopExitPolicy? exit, List<CustomLoopValidationError> errors)
    {
        if (exit is null)
        {
            Add(errors, "exit_policy_required", "exitPolicy", "Exit policy is required.");
            return;
        }

        if (exit.MaxAdditionalIterations < CustomLoopLimits.MinAdditionalIterations || exit.MaxAdditionalIterations > CustomLoopLimits.MaxAdditionalIterations)
        {
            Add(errors, "additional_iterations_out_of_range", "exitPolicy.maxAdditionalIterations", $"Maximum additional iterations must be between {CustomLoopLimits.MinAdditionalIterations} and {CustomLoopLimits.MaxAdditionalIterations}.");
        }

        ValidateText(exit.DecisionInstruction, "exit_decision_instruction_required", "exit_decision_instruction_too_long", "exitPolicy.decisionInstruction", CustomLoopLimits.MaxInstructionCharacters, required: exit.MaxAdditionalIterations > 0, errors);
        ValidateNodeContextPolicy(exit.ContextPolicy, "exitPolicy.contextPolicy", errors);
    }

    private static void ValidateNodeContextPolicy(CustomLoopNodeContextPolicy? policy, string field, List<CustomLoopValidationError> errors)
    {
        if (policy is null)
        {
            Add(errors, "node_context_policy_required", field, "Node context policy is required.");
            return;
        }

        if (!Enum.IsDefined(policy.Mode) || policy.Mode == CustomLoopContextPolicyMode.Unknown)
        {
            Add(errors, "unsupported_context_policy_mode", $"{field}.mode", "Node context policy mode must be inherit or custom.");
            return;
        }

        if (policy.Mode == CustomLoopContextPolicyMode.Inherit && policy.CustomPolicy is not null)
        {
            Add(errors, "unexpected_custom_context_policy", $"{field}.customPolicy", "Inherited node context cannot also persist a custom policy.");
            return;
        }

        if (policy.Mode == CustomLoopContextPolicyMode.Custom && policy.CustomPolicy is null)
        {
            Add(errors, "custom_context_policy_required", $"{field}.customPolicy", "Custom node context mode requires typed context-in and context-out settings.");
            return;
        }

        if (policy.CustomPolicy is not null)
        {
            ValidateResolvedContextPolicy(policy.CustomPolicy, $"{field}.customPolicy", errors);
        }
    }

    private static void ValidateResolvedContextPolicy(CustomLoopContextPolicy? policy, string field, List<CustomLoopValidationError> errors)
    {
        if (policy is null)
        {
            Add(errors, "context_policy_required", field, "Typed context policy is required.");
            return;
        }

        if (policy.ContextIn is null)
        {
            Add(errors, "context_in_required", $"{field}.contextIn", "Typed context-in settings are required.");
        }

        if (policy.ContextOut is null)
        {
            Add(errors, "context_out_required", $"{field}.contextOut", "Typed context-out settings are required.");
        }
    }

    private static void ValidateText(string? value, string requiredCode, string lengthCode, string field, int maxLength, bool required, List<CustomLoopValidationError> errors)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value))
        {
            Add(errors, requiredCode, field, $"{field} is required.");
            return;
        }

        if (value.Length > maxLength)
        {
            Add(errors, lengthCode, field, $"{field} cannot exceed {maxLength} characters.");
        }

        if (ContainsUnsafeCharacters(value))
        {
            Add(errors, "unsafe_text_characters", field, $"{field} contains unsupported control or invalid Unicode characters.");
        }
    }

    private static void ValidateArtifactId(string? value, string field, List<CustomLoopValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, CustomLoopLimits.MaxArtifactIdCharacters))
        {
            Add(errors, "invalid_artifact_id", field, $"{field} must be a filename-safe lowercase identifier of at most {CustomLoopLimits.MaxArtifactIdCharacters} characters.");
        }
    }

    private static void ValidateOperationId(string? value, List<CustomLoopValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, CustomLoopLimits.MaxMutationOperationIdCharacters))
        {
            Add(errors, "invalid_mutation_operation_id", "lastMutationOperationId", $"Mutation operation id must be a filename-safe lowercase identifier of at most {CustomLoopLimits.MaxMutationOperationIdCharacters} characters.");
        }
    }

    private static bool IsSha256Hex(string? value)
    {
        return value is { Length: CustomLoopLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsUtcTimestamp(DateTimeOffset value)
    {
        return value != default && value.Offset == TimeSpan.Zero;
    }

    private static bool ContainsUnsafeCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character) || char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(List<CustomLoopValidationError> errors, string code, string field, string message)
    {
        errors.Add(new CustomLoopValidationError(code, field, message));
    }
}
