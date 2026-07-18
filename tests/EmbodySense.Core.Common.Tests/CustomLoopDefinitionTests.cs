using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Common.Tests;

public sealed class CustomLoopDefinitionTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 16, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Seed_is_valid_and_matches_the_approved_prompt_only_defaults()
    {
        var definition = ValidDefinition();

        var result = CustomLoopDefinitionValidator.Validate(definition);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(CustomLoopDefinition.CurrentSchemaVersion, definition.SchemaVersion);
        Assert.Equal(1, definition.DefinitionVersion);
        Assert.Equal(CreatedAtUtc, definition.CreatedAtUtc);
        Assert.Equal(CreatedAtUtc, definition.UpdatedAtUtc);
        Assert.Equal("Untitled loop", definition.DisplayName);
        Assert.Empty(definition.Description);
        Assert.Equal("role-default", definition.RoleId);
        Assert.Equal(CustomLoopTriggerPromptSource.Invocation, definition.TriggerPolicy.PromptSource);
        Assert.Empty(definition.TriggerPolicy.PresetPrompt);
        Assert.False(definition.TriggerPolicy.IncludeInvokingConversation);
        Assert.Empty(definition.ToolAssignments);
        Assert.Single(definition.InferenceSteps);
        Assert.Equal("First step", definition.InferenceSteps[0].Name);
        Assert.Equal(CustomLoopDefinition.DefaultInferenceInstruction, definition.InferenceSteps[0].Instruction);
        Assert.Equal(CustomLoopContextPolicyMode.Inherit, definition.InferenceSteps[0].ContextPolicy.Mode);
        Assert.Null(definition.InferenceSteps[0].ContextPolicy.CustomPolicy);
        Assert.Equal(0, definition.ExitPolicy.MaxAdditionalIterations);
        Assert.Equal(CustomLoopDefinition.DefaultExitDecisionInstruction, definition.ExitPolicy.DecisionInstruction);
        Assert.Equal(CustomLoopContextPolicyMode.Inherit, definition.ExitPolicy.ContextPolicy.Mode);
        Assert.True(CustomLoopDefinitionContentHash.Matches(definition));
    }

    [Fact]
    public void Prototype_context_defaults_keep_inference_and_exit_product_context_separate()
    {
        var defaults = CustomLoopContextDefaults.CreatePrototypeDefaults();

        Assert.True(defaults.Inference.ContextIn.IncludeRoleContext);
        Assert.True(defaults.Inference.ContextIn.IncludeTriggerPrompt);
        Assert.False(defaults.Inference.ContextIn.IncludeInvokingConversation);
        Assert.True(defaults.Inference.ContextIn.IncludeEarlierRetainedOutputs);
        Assert.True(defaults.Inference.ContextIn.IncludePreviousIterationResult);
        Assert.True(defaults.Inference.ContextOut.RetainForLoopReasoning);
        Assert.False(defaults.Inference.ContextOut.PublishToInvokingConversation);
        Assert.Equal(defaults.Inference.ContextIn, defaults.Exit.ContextIn);
        Assert.False(defaults.Exit.ContextOut.RetainForLoopReasoning);
        Assert.True(defaults.Exit.ContextOut.PublishToInvokingConversation);
    }

    [Fact]
    public void Typed_custom_context_overrides_are_valid_for_inference_and_exit()
    {
        var definition = ValidDefinition();
        var customPolicy = new CustomLoopContextPolicy(
            new CustomLoopContextInputPolicy(false, false, true, false, false),
            new CustomLoopContextOutputPolicy(false, false));
        definition = definition with
        {
            TriggerPolicy = definition.TriggerPolicy with { IncludeInvokingConversation = true },
            InferenceSteps = [definition.InferenceSteps[0] with { ContextPolicy = CustomLoopNodeContextPolicy.Override(customPolicy) }],
            ExitPolicy = definition.ExitPolicy with { ContextPolicy = CustomLoopNodeContextPolicy.Override(customPolicy) }
        };
        definition = CustomLoopDefinitionContentHash.Apply(definition);

        var result = CustomLoopDefinitionValidator.Validate(definition);

        Assert.True(result.IsValid);
        Assert.Equal(CustomLoopContextPolicyMode.Custom, definition.InferenceSteps[0].ContextPolicy.Mode);
        Assert.Same(customPolicy, definition.InferenceSteps[0].ContextPolicy.CustomPolicy);
        Assert.Throws<ArgumentNullException>(() => CustomLoopNodeContextPolicy.Override(null!));
    }

    [Fact]
    public void Serialized_contract_contains_only_typed_context_and_authored_prompt_locations()
    {
        var json = JsonSerializer.Serialize(ValidDefinition());

        Assert.Contains("ContextDefaults", json, StringComparison.Ordinal);
        Assert.Contains("ContextIn", json, StringComparison.Ordinal);
        Assert.Contains("ContextOut", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AdditionalFixedContext", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FreeFormContext", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CustomLoopTriggerPromptSource.Invocation, "")]
    [InlineData(CustomLoopTriggerPromptSource.Preset, "A saved command-style prompt.")]
    [InlineData(CustomLoopTriggerPromptSource.None, "")]
    public void Closed_trigger_sources_are_valid(CustomLoopTriggerPromptSource source, string presetPrompt)
    {
        var definition = ValidDefinition() with { TriggerPolicy = new CustomLoopTriggerPolicy(source, presetPrompt, IncludeInvokingConversation: true) };
        definition = CustomLoopDefinitionContentHash.Apply(definition);

        Assert.True(CustomLoopDefinitionValidator.Validate(definition).IsValid);
    }

    [Fact]
    public void Maximum_definition_bounds_are_valid_and_produce_sixty_five_attempts()
    {
        var definition = ValidDefinition() with
        {
            DisplayName = new string('n', CustomLoopLimits.MaxNameCharacters),
            Description = new string('d', CustomLoopLimits.MaxDescriptionCharacters),
            TriggerPolicy = new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Preset, new string('p', CustomLoopLimits.MaxPresetPromptCharacters), IncludeInvokingConversation: true),
            InferenceSteps = Enumerable.Range(1, CustomLoopLimits.MaxInferenceSteps)
                .Select(index => new CustomLoopInferenceStep($"step-{index}", new string('n', CustomLoopLimits.MaxNameCharacters), new string('i', CustomLoopLimits.MaxInstructionCharacters), CustomLoopNodeContextPolicy.Inherit()))
                .ToArray(),
            ToolAssignments = [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search],
            ExitPolicy = new CustomLoopExitPolicy(CustomLoopLimits.MaxAdditionalIterations, new string('e', CustomLoopLimits.MaxInstructionCharacters), CustomLoopNodeContextPolicy.Inherit())
        };
        definition = CustomLoopDefinitionContentHash.Apply(definition);

        Assert.True(CustomLoopDefinitionValidator.Validate(definition).IsValid);
        Assert.Equal(CustomLoopLimits.MaxModelAttemptsPerRun, CustomLoopLimits.GetMaximumModelAttempts(definition.InferenceSteps.Length, definition.ExitPolicy.MaxAdditionalIterations));
        Assert.Equal(1, CustomLoopLimits.GetMaximumModelAttempts(1, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 11)]
    public void Attempt_calculation_rejects_definition_bounds(int inferenceSteps, int additionalIterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomLoopLimits.GetMaximumModelAttempts(inferenceSteps, additionalIterations));
    }

    [Fact]
    public void Canonical_hash_is_stable_normalized_and_excludes_the_hash_field()
    {
        var definition = ValidDefinition();
        var decomposed = definition with { DisplayName = "Cafe\u0301" };
        var composed = definition with { DisplayName = "Caf\u00e9" };

        var decomposedHash = CustomLoopDefinitionContentHash.Compute(decomposed);
        var composedHash = CustomLoopDefinitionContentHash.Compute(composed);

        Assert.Equal(composedHash, decomposedHash);
        Assert.Equal(definition.ContentHash, CustomLoopDefinitionContentHash.Compute(definition with { ContentHash = new string('f', 64) }));
        Assert.False(CustomLoopDefinitionContentHash.Matches(definition with { ContentHash = new string('f', 64) }));
        Assert.True(CustomLoopDefinitionContentHash.Matches(CustomLoopDefinitionContentHash.Apply(decomposed)));
        Assert.Throws<ArgumentNullException>(() => CustomLoopDefinitionContentHash.Compute(null!));
        Assert.Throws<ArgumentNullException>(() => CustomLoopDefinitionContentHash.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => CustomLoopDefinitionContentHash.Matches(null!));
    }

    [Fact]
    public void Validation_reports_every_visible_problem_in_one_result()
    {
        var definition = ValidDefinition() with
        {
            SchemaVersion = 99,
            DisplayName = " ",
            InferenceSteps = [],
            ToolAssignments = [CustomLoopToolAssignment.Unknown],
            ExitPolicy = ValidDefinition().ExitPolicy with { MaxAdditionalIterations = 11 }
        };

        var result = CustomLoopDefinitionValidator.Validate(definition);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "unsupported_schema_version");
        Assert.Contains(result.Errors, error => error.Code == "display_name_required");
        Assert.Contains(result.Errors, error => error.Code == "inference_step_count_out_of_range");
        Assert.Contains(result.Errors, error => error.Code == "unsupported_tool_assignment");
        Assert.Contains(result.Errors, error => error.Code == "additional_iterations_out_of_range");
    }

    [Fact]
    public void Null_definition_is_a_structured_validation_rejection()
    {
        var result = CustomLoopDefinitionValidator.Validate(null);

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("definition_required", error.Code);
        Assert.Equal("$", error.Field);
    }

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void Validation_rejects_invalid_closed_contract_states(CustomLoopDefinition definition, string expectedCode, string expectedField)
    {
        var result = CustomLoopDefinitionValidator.Validate(definition);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == expectedCode && error.Field == expectedField);
    }

    [Theory]
    [MemberData(nameof(InvalidHashShapes))]
    public void Canonical_hashing_remains_deterministic_for_structurally_invalid_shapes(CustomLoopDefinition definition)
    {
        var first = CustomLoopDefinitionContentHash.Compute(definition);
        var second = CustomLoopDefinitionContentHash.Compute(definition);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
    }

    public static IEnumerable<object[]> InvalidDefinitions()
    {
        var definition = ValidDefinition();
        var step = definition.InferenceSteps[0];
        var resolved = definition.ContextDefaults.Inference;
        var nonUtc = CreatedAtUtc.ToOffset(TimeSpan.FromHours(-5));

        yield return [definition with { SchemaVersion = 2 }, "unsupported_schema_version", "schemaVersion"];
        yield return [definition with { Id = "../escape" }, "invalid_artifact_id", "id"];
        yield return [definition with { Id = "loop." }, "invalid_artifact_id", "id"];
        yield return [definition with { Id = "CON" }, "invalid_artifact_id", "id"];
        yield return [definition with { Id = "con" }, "invalid_artifact_id", "id"];
        yield return [definition with { Id = new string('a', CustomLoopLimits.MaxArtifactIdCharacters + 1) }, "invalid_artifact_id", "id"];
        yield return [definition with { DefinitionVersion = 0 }, "invalid_definition_version", "definitionVersion"];
        yield return [definition with { ContentHash = "not-a-hash" }, "invalid_content_hash", "contentHash"];
        yield return [definition with { ContentHash = new string('F', 64) }, "invalid_content_hash", "contentHash"];
        yield return [definition with { ContentHash = new string('f', 64) }, "content_hash_mismatch", "contentHash"];
        yield return [definition with { CreatedAtUtc = default }, "invalid_created_timestamp", "createdAtUtc"];
        yield return [definition with { CreatedAtUtc = nonUtc }, "invalid_created_timestamp", "createdAtUtc"];
        yield return [definition with { UpdatedAtUtc = default }, "invalid_updated_timestamp", "updatedAtUtc"];
        yield return [definition with { UpdatedAtUtc = nonUtc }, "invalid_updated_timestamp", "updatedAtUtc"];
        yield return [definition with { UpdatedAtUtc = definition.CreatedAtUtc.AddSeconds(-1) }, "invalid_timestamp_order", "updatedAtUtc"];
        yield return [definition with { RoleId = "" }, "invalid_artifact_id", "roleId"];
        yield return [definition with { LastMutationOperationId = "bad/op" }, "invalid_mutation_operation_id", "lastMutationOperationId"];
        yield return [definition with { DisplayName = "" }, "display_name_required", "displayName"];
        yield return [definition with { DisplayName = new string('n', CustomLoopLimits.MaxNameCharacters + 1) }, "display_name_too_long", "displayName"];
        yield return [definition with { DisplayName = "unsafe\u0001name" }, "unsafe_text_characters", "displayName"];
        yield return [definition with { Description = null! }, "description_required", "description"];
        yield return [definition with { Description = new string('d', CustomLoopLimits.MaxDescriptionCharacters + 1) }, "description_too_long", "description"];
        yield return [definition with { Description = "bad\ud800" }, "unsafe_text_characters", "description"];
        yield return [definition with { TriggerPolicy = null! }, "trigger_policy_required", "triggerPolicy"];
        yield return [definition with { TriggerPolicy = definition.TriggerPolicy with { PromptSource = CustomLoopTriggerPromptSource.Unknown } }, "unsupported_trigger_source", "triggerPolicy.promptSource"];
        yield return [definition with { TriggerPolicy = definition.TriggerPolicy with { PromptSource = (CustomLoopTriggerPromptSource)99 } }, "unsupported_trigger_source", "triggerPolicy.promptSource"];
        yield return [definition with { TriggerPolicy = new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Preset, " ", false) }, "preset_prompt_required", "triggerPolicy.presetPrompt"];
        yield return [definition with { TriggerPolicy = new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, "unused", false) }, "unexpected_preset_prompt", "triggerPolicy.presetPrompt"];
        yield return [definition with { TriggerPolicy = new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.None, "unused", false) }, "unexpected_preset_prompt", "triggerPolicy.presetPrompt"];
        yield return [definition with { TriggerPolicy = definition.TriggerPolicy with { PresetPrompt = null! } }, "preset_prompt_required", "triggerPolicy.presetPrompt"];
        yield return [definition with { TriggerPolicy = new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Preset, new string('p', CustomLoopLimits.MaxPresetPromptCharacters + 1), false) }, "preset_prompt_too_long", "triggerPolicy.presetPrompt"];
        yield return [definition with { ContextDefaults = null! }, "context_defaults_required", "contextDefaults"];
        yield return [definition with { ContextDefaults = definition.ContextDefaults with { Inference = null! } }, "context_policy_required", "contextDefaults.inference"];
        yield return [definition with { ContextDefaults = definition.ContextDefaults with { Exit = null! } }, "context_policy_required", "contextDefaults.exit"];
        yield return [definition with { ContextDefaults = definition.ContextDefaults with { Inference = resolved with { ContextIn = null! } } }, "context_in_required", "contextDefaults.inference.contextIn"];
        yield return [definition with { ContextDefaults = definition.ContextDefaults with { Exit = resolved with { ContextOut = null! } } }, "context_out_required", "contextDefaults.exit.contextOut"];
        yield return [definition with { InferenceSteps = null! }, "inference_steps_required", "inferenceSteps"];
        yield return [definition with { InferenceSteps = [] }, "inference_step_count_out_of_range", "inferenceSteps"];
        yield return [definition with { InferenceSteps = Enumerable.Range(0, 6).Select(index => step with { Id = $"step-{index}" }).ToArray() }, "inference_step_count_out_of_range", "inferenceSteps"];
        yield return [definition with { InferenceSteps = [null!] }, "inference_step_required", "inferenceSteps[0]"];
        yield return [definition with { InferenceSteps = [step, step] }, "duplicate_inference_step_id", "inferenceSteps[1].id"];
        yield return [definition with { InferenceSteps = [step with { Id = "bad/id" }] }, "invalid_artifact_id", "inferenceSteps[0].id"];
        yield return [definition with { InferenceSteps = [step with { Name = "" }] }, "inference_step_name_required", "inferenceSteps[0].name"];
        yield return [definition with { InferenceSteps = [step with { Name = new string('n', CustomLoopLimits.MaxNameCharacters + 1) }] }, "inference_step_name_too_long", "inferenceSteps[0].name"];
        yield return [definition with { InferenceSteps = [step with { Instruction = "" }] }, "inference_instruction_required", "inferenceSteps[0].instruction"];
        yield return [definition with { InferenceSteps = [step with { Instruction = new string('i', CustomLoopLimits.MaxInstructionCharacters + 1) }] }, "inference_instruction_too_long", "inferenceSteps[0].instruction"];
        yield return [definition with { InferenceSteps = [step with { ContextPolicy = null! }] }, "node_context_policy_required", "inferenceSteps[0].contextPolicy"];
        yield return [definition with { InferenceSteps = [step with { ContextPolicy = new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Unknown, null) }] }, "unsupported_context_policy_mode", "inferenceSteps[0].contextPolicy.mode"];
        yield return [definition with { InferenceSteps = [step with { ContextPolicy = new CustomLoopNodeContextPolicy((CustomLoopContextPolicyMode)99, null) }] }, "unsupported_context_policy_mode", "inferenceSteps[0].contextPolicy.mode"];
        yield return [definition with { InferenceSteps = [step with { ContextPolicy = new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Inherit, resolved) }] }, "unexpected_custom_context_policy", "inferenceSteps[0].contextPolicy.customPolicy"];
        yield return [definition with { InferenceSteps = [step with { ContextPolicy = new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Custom, null) }] }, "custom_context_policy_required", "inferenceSteps[0].contextPolicy.customPolicy"];
        yield return [definition with { InferenceSteps = [step with { ContextPolicy = CustomLoopNodeContextPolicy.Override(resolved with { ContextIn = null! }) }] }, "context_in_required", "inferenceSteps[0].contextPolicy.customPolicy.contextIn"];
        yield return [definition with { ToolAssignments = null! }, "tool_assignments_required", "toolAssignments"];
        yield return [definition with { ToolAssignments = [CustomLoopToolAssignment.Unknown] }, "unsupported_tool_assignment", "toolAssignments[0]"];
        yield return [definition with { ToolAssignments = [(CustomLoopToolAssignment)99] }, "unsupported_tool_assignment", "toolAssignments[0]"];
        yield return [definition with { ToolAssignments = [CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Read] }, "duplicate_tool_assignment", "toolAssignments[1]"];
        yield return [definition with { ExitPolicy = null! }, "exit_policy_required", "exitPolicy"];
        yield return [definition with { ExitPolicy = definition.ExitPolicy with { MaxAdditionalIterations = -1 } }, "additional_iterations_out_of_range", "exitPolicy.maxAdditionalIterations"];
        yield return [definition with { ExitPolicy = definition.ExitPolicy with { MaxAdditionalIterations = 11 } }, "additional_iterations_out_of_range", "exitPolicy.maxAdditionalIterations"];
        yield return [definition with { ExitPolicy = definition.ExitPolicy with { MaxAdditionalIterations = 1, DecisionInstruction = "" } }, "exit_decision_instruction_required", "exitPolicy.decisionInstruction"];
        yield return [definition with { ExitPolicy = definition.ExitPolicy with { DecisionInstruction = new string('e', CustomLoopLimits.MaxInstructionCharacters + 1) } }, "exit_decision_instruction_too_long", "exitPolicy.decisionInstruction"];
        yield return [definition with { ExitPolicy = definition.ExitPolicy with { ContextPolicy = new CustomLoopNodeContextPolicy(CustomLoopContextPolicyMode.Custom, null) } }, "custom_context_policy_required", "exitPolicy.contextPolicy.customPolicy"];
    }

    public static IEnumerable<object[]> InvalidHashShapes()
    {
        var definition = ValidDefinition();
        var resolved = definition.ContextDefaults.Inference;

        yield return [definition with { TriggerPolicy = null! }];
        yield return [definition with { TriggerPolicy = definition.TriggerPolicy with { PromptSource = (CustomLoopTriggerPromptSource)99 } }];
        yield return [definition with { ContextDefaults = null! }];
        yield return [definition with { ContextDefaults = definition.ContextDefaults with { Inference = null! } }];
        yield return [definition with { ContextDefaults = definition.ContextDefaults with { Inference = resolved with { ContextIn = null! }, Exit = resolved with { ContextOut = null! } } }];
        yield return [definition with { InferenceSteps = null! }];
        yield return [definition with { InferenceSteps = [null!] }];
        yield return [definition with { InferenceSteps = [definition.InferenceSteps[0] with { ContextPolicy = null! }] }];
        yield return [definition with { InferenceSteps = [definition.InferenceSteps[0] with { ContextPolicy = new CustomLoopNodeContextPolicy((CustomLoopContextPolicyMode)99, null) }] }];
        yield return [definition with { ToolAssignments = null! }];
        yield return [definition with { ToolAssignments = [(CustomLoopToolAssignment)99] }];
        yield return [definition with { ExitPolicy = null! }];
    }

    private static CustomLoopDefinition ValidDefinition()
    {
        return CustomLoopDefinition.CreateSeed("loop-one", "role-default", "step-one", "operation-one", CreatedAtUtc);
    }
}
