using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.Loops.Custom.Graph;

/// <summary>Computes the canonical schema-1 executable digest for governed custom-loop graphs.</summary>
public static class GovernedLoopExecutableHash
{
    /// <summary>Computes a lowercase SHA-256 digest while deliberately excluding graph, revision, display, and layout identity.</summary>
    /// <param name="graph">The validated canonical graph.</param>
    /// <returns>The lowercase executable content digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="graph"/> is <see langword="null"/>.</exception>
    public static string Compute(GovernedLoopGraphDefinition graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", graph.SchemaVersion);
        writer.WriteString("purpose", graph.Purpose);
        writer.WriteString("entryNodeId", graph.EntryNodeId);
        writer.WritePropertyName("owningRole");
        writer.WriteStartObject();
        writer.WriteString("contentHash", graph.OwningRole.ContentHash);
        writer.WriteNumber("revision", graph.OwningRole.Identity.Revision);
        writer.WriteString("roleId", graph.OwningRole.Identity.RoleId);
        writer.WriteEndObject();
        WriteStrings(writer, "terminalNodeIds", graph.TerminalNodeIds);
        writer.WriteString("defaultModelRoutingPolicyHash", graph.DefaultModelRoutingPolicy.ContentHash);
        WriteAuthority(writer, graph.AuthorityCeiling);
        WriteSchemas(writer, graph.ValueSchemas);
        WriteNodes(writer, graph.Nodes);
        WriteEdges(writer, graph.ControlEdges);
        WriteBindings(writer, graph.Bindings);
        WriteOutput(writer, graph.OutputContract);
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void WriteSchemas(Utf8JsonWriter writer, IReadOnlyList<GovernedLoopValueSchemaDefinition> schemas)
    {
        writer.WritePropertyName("valueSchemas");
        writer.WriteStartArray();
        foreach (var schema in schemas)
        {
            writer.WriteStartObject();
            writer.WriteString("id", schema.Id);
            writer.WriteString("kind", GovernedLoopValueKindVocabulary.ToCanonical(schema.Kind));
            writer.WriteBoolean("nullable", schema.Nullable);
            writer.WriteString("format", schema.Format);
            writer.WriteString("elementSchemaId", schema.ElementSchemaId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNodes(Utf8JsonWriter writer, IReadOnlyList<GovernedLoopNodeDefinition> nodes)
    {
        writer.WritePropertyName("nodes");
        writer.WriteStartArray();
        foreach (var node in nodes)
        {
            writer.WriteStartObject();
            writer.WriteString("id", node.Id);
            writer.WriteString("kind", ToCanonical(node.Descriptor.Kind));
            writer.WriteString("typeId", node.Descriptor.TypeId);
            writer.WriteNumber("descriptorVersion", node.Descriptor.Version);
            if (node.HumanInputConfiguration is { } humanInputConfiguration)
            {
                WriteHumanInputConfiguration(writer, humanInputConfiguration);
            }
            writer.WriteString("modelRoutingPolicyHash", node.ModelRoutingPolicy?.ContentHash);
            if (node.RetryPolicy is { } retryPolicy)
            {
                writer.WriteString("retryPolicyHash", retryPolicy.ContentHash);
            }
            writer.WritePropertyName("authoredInputDataClasses");
            if (node.AuthoredInputDataClasses is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var dataClass in node.AuthoredInputDataClasses)
                {
                    writer.WriteStringValue(dataClass.Value);
                }
                writer.WriteEndArray();
            }
            WriteAuthority(writer, node.AuthorityCeiling);
            writer.WritePropertyName("parameters");
            writer.WriteStartObject();
            foreach (var parameter in node.Parameters)
            {
                writer.WriteString(parameter.Key, parameter.Value);
            }

            writer.WriteEndObject();
            writer.WritePropertyName("ports");
            writer.WriteStartArray();
            foreach (var port in node.Ports)
            {
                writer.WriteStartObject();
                writer.WriteString("id", port.Id);
                writer.WriteString("direction", ToCanonical(port.Direction));
                writer.WriteString("bindingKind", ToCanonical(port.BindingKind));
                writer.WriteString("valueSchemaId", port.ValueSchemaId);
                writer.WriteBoolean("required", port.Required);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteHumanInputConfiguration(Utf8JsonWriter writer, GovernedLoopHumanInputNodeConfiguration configuration)
    {
        writer.WritePropertyName("humanInputConfiguration");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", configuration.SchemaVersion);
        writer.WriteString("requestSchemaReference", configuration.RequestSchemaReference);
        writer.WriteString("purpose", configuration.Purpose);
        writer.WriteString("prompt", configuration.Prompt);
        writer.WriteString("privacyClass", ToCanonical(configuration.PrivacyClass));
        writer.WriteString("timeoutPolicyReference", configuration.TimeoutPolicyReference);
        writer.WriteString("failurePolicyReference", configuration.FailurePolicyReference);
        WriteHumanInputResponseSchema(writer, configuration.ResponseSchema);
        writer.WritePropertyName("eligibleRespondents");
        if (configuration.EligibleRespondents is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var respondent in configuration.EligibleRespondents)
            {
                if (respondent is null)
                {
                    writer.WriteNullValue();
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("respondentId", respondent.RespondentId);
                writer.WriteString("respondentRoleId", respondent.RespondentRoleId);
                writer.WriteString("routingReference", respondent.RoutingReference);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WritePropertyName("responsePolicy");
        if (configuration.ResponsePolicy is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("kind", ToCanonical(configuration.ResponsePolicy.Kind));
            WriteNullableInteger(writer, "requiredResponseCount", configuration.ResponsePolicy.RequiredResponseCount);
            writer.WritePropertyName("orderedRoleIds");
            if (configuration.ResponsePolicy.OrderedRoleIds is not { } roles)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var roleId in roles)
                {
                    writer.WriteStringValue(roleId);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteHumanInputResponseSchema(Utf8JsonWriter writer, HumanInputResponseSchema? schema)
    {
        writer.WritePropertyName("responseSchema");
        if (schema is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", ToCanonical(schema.Kind));
        WriteNullableInteger(writer, "maxTextCharacters", schema.MaxTextCharacters);
        WriteHumanInputChoices(writer, "choices", schema.Choices);
        writer.WritePropertyName("structuredFields");
        if (schema.StructuredFields is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var field in schema.StructuredFields)
            {
                if (field is null)
                {
                    writer.WriteNullValue();
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("fieldId", field.FieldId);
                writer.WriteString("kind", ToCanonical(field.Kind));
                writer.WriteBoolean("required", field.Required);
                WriteNullableInteger(writer, "maxTextCharacters", field.MaxTextCharacters);
                WriteHumanInputChoices(writer, "choices", field.Choices);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WritePropertyName("referencePolicy");
        if (schema.ReferencePolicy is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("kind", ToCanonical(schema.ReferencePolicy.Kind));
            writer.WriteNumber("maxReferenceCharacters", schema.ReferencePolicy.MaxReferenceCharacters);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteHumanInputChoices(Utf8JsonWriter writer, string name, HumanInputChoice[]? choices)
    {
        writer.WritePropertyName(name);
        if (choices is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var choice in choices)
        {
            if (choice is null)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("choiceId", choice.ChoiceId);
            writer.WriteString("displayText", choice.DisplayText);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteNullableInteger(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } bounded)
        {
            writer.WriteNumber(name, bounded);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteEdges(Utf8JsonWriter writer, IReadOnlyList<GovernedLoopControlEdgeDefinition> edges)
    {
        writer.WritePropertyName("controlEdges");
        writer.WriteStartArray();
        foreach (var edge in edges)
        {
            writer.WriteStartObject();
            writer.WriteString("id", edge.Id);
            writer.WriteString("fromNodeId", edge.FromNodeId);
            writer.WriteString("toNodeId", edge.ToNodeId);
            writer.WriteString("condition", ToCanonical(edge.Condition));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteBindings(Utf8JsonWriter writer, IReadOnlyList<GovernedLoopBindingDefinition> bindings)
    {
        writer.WritePropertyName("bindings");
        writer.WriteStartArray();
        foreach (var binding in bindings)
        {
            writer.WriteStartObject();
            writer.WriteString("id", binding.Id);
            writer.WriteString("kind", ToCanonical(binding.Kind));
            writer.WriteString("fromNodeId", binding.FromNodeId);
            writer.WriteString("fromPortId", binding.FromPortId);
            writer.WriteString("toNodeId", binding.ToNodeId);
            writer.WriteString("toPortId", binding.ToPortId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOutput(Utf8JsonWriter writer, GovernedLoopOutputContract contract)
    {
        writer.WritePropertyName("outputContract");
        writer.WriteStartObject();
        writer.WriteString("summary", contract.Summary);
        writer.WritePropertyName("outputs");
        writer.WriteStartArray();
        foreach (var output in contract.Outputs)
        {
            writer.WriteStartObject();
            writer.WriteString("id", output.Id);
            writer.WriteString("valueSchemaId", output.ValueSchemaId);
            writer.WriteString("sourceNodeId", output.SourceNodeId);
            writer.WriteString("sourcePortId", output.SourcePortId);
            writer.WriteBoolean("required", output.Required);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAuthority(Utf8JsonWriter writer, GovernedLoopAuthorityCeiling ceiling)
    {
        WriteStrings(writer, "authorityCeiling", ceiling.CapabilityIds);
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string ToCanonical(GovernedLoopNodeKind value)
    {
        return value switch
        {
            GovernedLoopNodeKind.Trigger => "trigger",
            GovernedLoopNodeKind.Inference => "inference",
            GovernedLoopNodeKind.Transform => "transform",
            GovernedLoopNodeKind.Validate => "validate",
            GovernedLoopNodeKind.State => "state",
            GovernedLoopNodeKind.Condition => "condition",
            GovernedLoopNodeKind.Join => "join",
            GovernedLoopNodeKind.Wait => "wait",
            GovernedLoopNodeKind.Action => "action",
            GovernedLoopNodeKind.HumanReview => "human-review",
            GovernedLoopNodeKind.HumanInput => "human-input",
            GovernedLoopNodeKind.ChildLoop => "child-loop",
            GovernedLoopNodeKind.Exit => "exit",
            GovernedLoopNodeKind.Fail => "fail",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static string ToCanonical(HumanInputPrivacyClass value)
        => value switch
        {
            HumanInputPrivacyClass.Private => "private",
            HumanInputPrivacyClass.Sensitive => "sensitive",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string ToCanonical(HumanInputResponseKind value)
        => value switch
        {
            HumanInputResponseKind.Text => "text",
            HumanInputResponseKind.Choice => "choice",
            HumanInputResponseKind.Confirmation => "confirmation",
            HumanInputResponseKind.Structured => "structured",
            HumanInputResponseKind.Reference => "reference",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string ToCanonical(HumanInputStructuredFieldKind value)
        => value switch
        {
            HumanInputStructuredFieldKind.Text => "text",
            HumanInputStructuredFieldKind.Choice => "choice",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string ToCanonical(HumanInputReferenceKind value)
        => value switch
        {
            HumanInputReferenceKind.Artifact => "artifact",
            HumanInputReferenceKind.Reference => "reference",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string ToCanonical(HumanInputResponsePolicyKind value)
        => value switch
        {
            HumanInputResponsePolicyKind.FirstValid => "first-valid",
            HumanInputResponsePolicyKind.Quorum => "quorum",
            HumanInputResponsePolicyKind.NamedRoles => "named-roles",
            HumanInputResponsePolicyKind.Merge => "merge",
            HumanInputResponsePolicyKind.ManualSelection => "manual-selection",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string ToCanonical(GovernedLoopBindingKind value)
    {
        return value switch
        {
            GovernedLoopBindingKind.Data => "data",
            GovernedLoopBindingKind.Context => "context",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static string ToCanonical(GovernedLoopPortDirection value)
    {
        return value switch
        {
            GovernedLoopPortDirection.Input => "input",
            GovernedLoopPortDirection.Output => "output",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static string ToCanonical(GovernedLoopControlCondition value)
    {
        return value switch
        {
            GovernedLoopControlCondition.Always => "always",
            GovernedLoopControlCondition.Success => "success",
            GovernedLoopControlCondition.Failure => "failure",
            GovernedLoopControlCondition.True => "true",
            GovernedLoopControlCondition.False => "false",
            GovernedLoopControlCondition.Timeout => "timeout",
            GovernedLoopControlCondition.Approved => "approved",
            GovernedLoopControlCondition.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }
}
