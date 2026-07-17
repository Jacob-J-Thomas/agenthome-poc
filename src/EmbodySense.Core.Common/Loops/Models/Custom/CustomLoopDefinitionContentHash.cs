using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Common.Loops.Models.Custom;

public static class CustomLoopDefinitionContentHash
{
    public static string Compute(CustomLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteDefinition(writer, definition);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    public static CustomLoopDefinition Apply(CustomLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition with { ContentHash = Compute(definition) };
    }

    public static bool Matches(CustomLoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var expected = Encoding.ASCII.GetBytes(Compute(definition));
        var actual = Encoding.ASCII.GetBytes(definition.ContentHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void WriteDefinition(Utf8JsonWriter writer, CustomLoopDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", definition.SchemaVersion);
        WriteString(writer, "id", definition.Id);
        writer.WriteNumber("definitionVersion", definition.DefinitionVersion);
        WriteTimestamp(writer, "createdAtUtc", definition.CreatedAtUtc);
        WriteTimestamp(writer, "updatedAtUtc", definition.UpdatedAtUtc);
        WriteString(writer, "displayName", definition.DisplayName);
        WriteString(writer, "description", definition.Description);
        WriteString(writer, "roleId", definition.RoleId);
        WriteTrigger(writer, definition.TriggerPolicy);
        WriteContextDefaults(writer, definition.ContextDefaults);
        WriteInferenceSteps(writer, definition.InferenceSteps);
        WriteToolAssignments(writer, definition.ToolAssignments);
        WriteExitPolicy(writer, definition.ExitPolicy);
        WriteString(writer, "lastMutationOperationId", definition.LastMutationOperationId);
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteTrigger(Utf8JsonWriter writer, CustomLoopTriggerPolicy? trigger)
    {
        writer.WritePropertyName("triggerPolicy");
        if (trigger is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteString(writer, "promptSource", ToCanonical(trigger.PromptSource));
        WriteString(writer, "presetPrompt", trigger.PresetPrompt);
        writer.WriteBoolean("includeInvokingConversation", trigger.IncludeInvokingConversation);
        writer.WriteEndObject();
    }

    private static void WriteContextDefaults(Utf8JsonWriter writer, CustomLoopContextDefaults? defaults)
    {
        writer.WritePropertyName("contextDefaults");
        if (defaults is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteResolvedContextPolicy(writer, "inference", defaults.Inference);
        WriteResolvedContextPolicy(writer, "exit", defaults.Exit);
        writer.WriteEndObject();
    }

    private static void WriteInferenceSteps(Utf8JsonWriter writer, CustomLoopInferenceStep[]? steps)
    {
        writer.WritePropertyName("inferenceSteps");
        if (steps is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var step in steps)
        {
            if (step is null)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartObject();
            WriteString(writer, "id", step.Id);
            WriteString(writer, "name", step.Name);
            WriteString(writer, "instruction", step.Instruction);
            WriteNodeContextPolicy(writer, "contextPolicy", step.ContextPolicy);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteToolAssignments(Utf8JsonWriter writer, CustomLoopToolAssignment[]? assignments)
    {
        writer.WritePropertyName("toolAssignments");
        if (assignments is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var assignment in assignments)
        {
            writer.WriteStringValue(ToCanonical(assignment));
        }

        writer.WriteEndArray();
    }

    private static void WriteExitPolicy(Utf8JsonWriter writer, CustomLoopExitPolicy? exit)
    {
        writer.WritePropertyName("exitPolicy");
        if (exit is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("maxAdditionalIterations", exit.MaxAdditionalIterations);
        WriteString(writer, "decisionInstruction", exit.DecisionInstruction);
        WriteNodeContextPolicy(writer, "contextPolicy", exit.ContextPolicy);
        writer.WriteEndObject();
    }

    private static void WriteNodeContextPolicy(Utf8JsonWriter writer, string propertyName, CustomLoopNodeContextPolicy? policy)
    {
        writer.WritePropertyName(propertyName);
        if (policy is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteString(writer, "mode", ToCanonical(policy.Mode));
        WriteResolvedContextPolicy(writer, "customPolicy", policy.CustomPolicy);
        writer.WriteEndObject();
    }

    private static void WriteResolvedContextPolicy(Utf8JsonWriter writer, string propertyName, CustomLoopContextPolicy? policy)
    {
        writer.WritePropertyName(propertyName);
        if (policy is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("contextIn");
        if (policy.ContextIn is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteBoolean("includeRoleContext", policy.ContextIn.IncludeRoleContext);
            writer.WriteBoolean("includeTriggerPrompt", policy.ContextIn.IncludeTriggerPrompt);
            writer.WriteBoolean("includeInvokingConversation", policy.ContextIn.IncludeInvokingConversation);
            writer.WriteBoolean("includeEarlierRetainedOutputs", policy.ContextIn.IncludeEarlierRetainedOutputs);
            writer.WriteBoolean("includePreviousIterationResult", policy.ContextIn.IncludePreviousIterationResult);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("contextOut");
        if (policy.ContextOut is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteBoolean("retainForLoopReasoning", policy.ContextOut.RetainForLoopReasoning);
            writer.WriteBoolean("publishToInvokingConversation", policy.ContextOut.PublishToInvokingConversation);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value.Normalize(NormalizationForm.FormC));
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string propertyName, DateTimeOffset value)
    {
        writer.WriteString(propertyName, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static string ToCanonical(CustomLoopTriggerPromptSource value)
    {
        return value switch
        {
            CustomLoopTriggerPromptSource.Invocation => "invocation",
            CustomLoopTriggerPromptSource.Preset => "preset",
            CustomLoopTriggerPromptSource.None => "none",
            _ => $"unknown:{(int)value}"
        };
    }

    private static string ToCanonical(CustomLoopContextPolicyMode value)
    {
        return value switch
        {
            CustomLoopContextPolicyMode.Inherit => "inherit",
            CustomLoopContextPolicyMode.Custom => "custom",
            _ => $"unknown:{(int)value}"
        };
    }

    private static string ToCanonical(CustomLoopToolAssignment value)
    {
        return value switch
        {
            CustomLoopToolAssignment.List => "list",
            CustomLoopToolAssignment.Read => "read",
            CustomLoopToolAssignment.Search => "search",
            _ => $"unknown:{(int)value}"
        };
    }
}
