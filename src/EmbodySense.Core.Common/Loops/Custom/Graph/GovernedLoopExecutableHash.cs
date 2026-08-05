using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

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
        writer.WriteString("owningRoleId", graph.OwningRoleId);
        WriteStrings(writer, "terminalNodeIds", graph.TerminalNodeIds);
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
            writer.WriteString("kind", ToCanonical(schema.Kind));
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

    private static string ToCanonical(GovernedLoopValueKind value)
    {
        return value switch
        {
            GovernedLoopValueKind.Text => "text",
            GovernedLoopValueKind.Boolean => "boolean",
            GovernedLoopValueKind.Integer => "integer",
            GovernedLoopValueKind.Number => "number",
            GovernedLoopValueKind.Object => "object",
            GovernedLoopValueKind.Array => "array",
            GovernedLoopValueKind.Binary => "binary",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

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
