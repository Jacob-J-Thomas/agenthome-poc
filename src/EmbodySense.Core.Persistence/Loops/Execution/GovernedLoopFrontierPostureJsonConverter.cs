using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Persistence.Loops.Execution;

/// <summary>Reads and writes the one exact canonical schema-1 durable execution-frontier shape.</summary>
internal sealed class GovernedLoopFrontierPostureJsonConverter : JsonConverter<GovernedLoopFrontierPosture>
{
    public override GovernedLoopFrontierPosture Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            RequireStartObject(ref reader, "A governed-loop frontier must be an object.");
            RequireSchema(ref reader, "schemaVersion");
            var workspaceId = RequireString(ref reader, "workspaceId");
            RequireProperty(ref reader, "binding");
            var binding = JsonSerializer.Deserialize<GovernedLoopExecutionBinding>(ref reader, options)
                ?? throw new JsonException("The governed-loop frontier binding is required.");
            var graphArtifactHash = RequireString(ref reader, "graphArtifactHash");
            var graphLayoutHash = RequireString(ref reader, "graphLayoutHash");
            var admissionReceiptHash = RequireString(ref reader, "admissionReceiptHash");
            RequireProperty(ref reader, "payload");
            var payload = ReadPayload(ref reader);
            RequireEndObject(ref reader, "The governed-loop frontier contains missing, reordered, or unsupported properties.");
            return GovernedLoopFrontierPosture.Create(
                binding,
                workspaceId,
                graphArtifactHash,
                graphLayoutHash,
                admissionReceiptHash,
                payload.FrontierVersion,
                payload.ConcurrencyCeiling,
                payload.Status,
                payload.Nodes,
                payload.UpdatedAtUtc,
                payload.ContentHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new JsonException("The governed-loop frontier is outside the schema-1 contract.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, GovernedLoopFrontierPosture value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", value.SchemaVersion);
        writer.WriteString("workspaceId", value.WorkspaceId);
        writer.WritePropertyName("binding");
        JsonSerializer.Serialize(writer, value.Binding, options);
        writer.WriteString("graphArtifactHash", value.GraphArtifactHash);
        writer.WriteString("graphLayoutHash", value.GraphLayoutHash);
        writer.WriteString("admissionReceiptHash", value.AdmissionReceiptHash);
        writer.WritePropertyName("payload");
        WritePayload(writer, value, options);
        writer.WriteEndObject();
    }

    private static (
        long FrontierVersion,
        int ConcurrencyCeiling,
        GovernedLoopFrontierStatus Status,
        GovernedLoopNodeExecutionEvidence[] Nodes,
        DateTimeOffset UpdatedAtUtc,
        string ContentHash) ReadPayload(ref Utf8JsonReader reader)
    {
        RequireStartObject(ref reader, "A governed-loop frontier payload must be an object.");
        RequireSchema(ref reader, "schemaVersion");
        var frontierVersion = RequireInt64(ref reader, "frontierVersion");
        var concurrencyCeiling = RequireInt32(ref reader, "concurrencyCeiling");
        RequireProperty(ref reader, "status");
        var status = ReadFrontierStatus(ref reader);
        RequireProperty(ref reader, "nodes");
        var nodes = ReadNodes(ref reader);
        var updatedAtUtc = RequireDateTimeOffset(ref reader, "updatedAtUtc");
        var contentHash = RequireString(ref reader, "contentHash");
        RequireEndObject(ref reader, "The governed-loop frontier payload contains missing, reordered, or unsupported properties.");
        return (frontierVersion, concurrencyCeiling, status, nodes, updatedAtUtc, contentHash);
    }

    private static GovernedLoopNodeExecutionEvidence[] ReadNodes(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Governed-loop frontier nodes must be an array.");
        }

        var nodes = new List<GovernedLoopNodeExecutionEvidence>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (nodes.Count == GovernedLoopExecutionLimits.MaxFrontierNodes)
            {
                throw new JsonException("Governed-loop frontier nodes exceed the schema-1 bound.");
            }

            nodes.Add(ReadNode(ref reader));
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("The governed-loop frontier node array is incomplete.");
        }

        return [.. nodes];
    }

    private static GovernedLoopNodeExecutionEvidence ReadNode(ref Utf8JsonReader reader)
    {
        RequireStartObject(ref reader, "Governed-loop frontier node evidence must be an object.");
        RequireSchema(ref reader, "schemaVersion");
        var planOrdinal = RequireInt32(ref reader, "planOrdinal");
        var nodeId = RequireString(ref reader, "nodeId");
        RequireProperty(ref reader, "descriptor");
        var descriptor = ReadDescriptor(ref reader);
        RequireProperty(ref reader, "incomingControlEdgeIds");
        var incoming = ReadIdentifiers(ref reader, GovernedLoopExecutionLimits.MaxIncomingEdges, "incoming control edges");
        RequireProperty(ref reader, "outgoingControlEdgeIds");
        var outgoing = ReadIdentifiers(ref reader, GovernedLoopExecutionLimits.MaxOutgoingEdges, "outgoing control edges");
        RequireProperty(ref reader, "status");
        var status = ReadNodeStatus(ref reader);
        var attempt = RequireNullableInt32(ref reader, "attempt");
        var attemptOperationId = RequireNullableString(ref reader, "attemptOperationId");
        var outcomeEvidenceId = RequireNullableString(ref reader, "outcomeEvidenceId");
        var outcomeEvidenceHash = RequireNullableString(ref reader, "outcomeEvidenceHash");
        RequireEndObject(ref reader, "Governed-loop frontier node evidence contains missing, reordered, or unsupported properties.");
        return GovernedLoopNodeExecutionEvidence.Create(
            planOrdinal,
            nodeId,
            descriptor,
            incoming,
            outgoing,
            status,
            attempt,
            attemptOperationId,
            outcomeEvidenceId,
            outcomeEvidenceHash);
    }

    private static GovernedLoopNodeDescriptor ReadDescriptor(ref Utf8JsonReader reader)
    {
        RequireStartObject(ref reader, "A governed-loop node descriptor must be an object.");
        RequireProperty(ref reader, "kind");
        var kind = ReadNodeKind(ref reader);
        var typeId = RequireString(ref reader, "typeId");
        var version = RequireInt32(ref reader, "version");
        RequireEndObject(ref reader, "The governed-loop node descriptor contains missing, reordered, or unsupported properties.");
        return new GovernedLoopNodeDescriptor(kind, typeId, version);
    }

    private static string[] ReadIdentifiers(ref Utf8JsonReader reader, int maximum, string description)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Governed-loop {description} must be an array.");
        }

        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (values.Count == maximum || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Governed-loop {description} are malformed or exceed their schema-1 bound.");
            }

            values.Add(reader.GetString() ?? throw new JsonException($"A governed-loop {description} identity is invalid."));
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException($"The governed-loop {description} array is incomplete.");
        }

        return [.. values];
    }

    private static void WritePayload(Utf8JsonWriter writer, GovernedLoopFrontierPosture frontier, JsonSerializerOptions options)
    {
        var payload = frontier.Payload;
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", payload.SchemaVersion);
        writer.WriteNumber("frontierVersion", payload.FrontierVersion);
        writer.WriteNumber("concurrencyCeiling", payload.ConcurrencyCeiling);
        writer.WritePropertyName("status");
        JsonSerializer.Serialize(writer, payload.Status, options);
        writer.WritePropertyName("nodes");
        writer.WriteStartArray();
        foreach (var node in payload.Nodes)
        {
            WriteNode(writer, node, options);
        }

        writer.WriteEndArray();
        writer.WriteString("updatedAtUtc", payload.UpdatedAtUtc);
        writer.WriteString("contentHash", payload.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, GovernedLoopNodeExecutionEvidence node, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", node.SchemaVersion);
        writer.WriteNumber("planOrdinal", node.PlanOrdinal);
        writer.WriteString("nodeId", node.NodeId);
        writer.WritePropertyName("descriptor");
        writer.WriteStartObject();
        writer.WritePropertyName("kind");
        JsonSerializer.Serialize(writer, node.Descriptor.Kind, options);
        writer.WriteString("typeId", node.Descriptor.TypeId);
        writer.WriteNumber("version", node.Descriptor.Version);
        writer.WriteEndObject();
        WriteIdentifiers(writer, "incomingControlEdgeIds", node.IncomingControlEdgeIds);
        WriteIdentifiers(writer, "outgoingControlEdgeIds", node.OutgoingControlEdgeIds);
        writer.WritePropertyName("status");
        JsonSerializer.Serialize(writer, node.Status, options);
        WriteNullableNumber(writer, "attempt", node.Attempt);
        writer.WriteString("attemptOperationId", node.AttemptOperationId);
        writer.WriteString("outcomeEvidenceId", node.OutcomeEvidenceId);
        writer.WriteString("outcomeEvidenceHash", node.OutcomeEvidenceHash);
        writer.WriteEndObject();
    }

    private static void WriteIdentifiers(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static GovernedLoopFrontierStatus ReadFrontierStatus(ref Utf8JsonReader reader)
        => RequireEnumText(ref reader, "frontier status") switch
        {
            "active" => GovernedLoopFrontierStatus.Active,
            "waiting" => GovernedLoopFrontierStatus.Waiting,
            "reviewBlocked" => GovernedLoopFrontierStatus.ReviewBlocked,
            "completed" => GovernedLoopFrontierStatus.Completed,
            "failed" => GovernedLoopFrontierStatus.Failed,
            "cancelled" => GovernedLoopFrontierStatus.Cancelled,
            _ => throw new JsonException("The governed-loop frontier status is unsupported."),
        };

    private static GovernedLoopNodeExecutionStatus ReadNodeStatus(ref Utf8JsonReader reader)
        => RequireEnumText(ref reader, "node status") switch
        {
            "ready" => GovernedLoopNodeExecutionStatus.Ready,
            "running" => GovernedLoopNodeExecutionStatus.Running,
            "completed" => GovernedLoopNodeExecutionStatus.Completed,
            "skipped" => GovernedLoopNodeExecutionStatus.Skipped,
            "waiting" => GovernedLoopNodeExecutionStatus.Waiting,
            "failed" => GovernedLoopNodeExecutionStatus.Failed,
            "reviewBlocked" => GovernedLoopNodeExecutionStatus.ReviewBlocked,
            _ => throw new JsonException("The governed-loop node status is unsupported."),
        };

    private static GovernedLoopNodeKind ReadNodeKind(ref Utf8JsonReader reader)
        => RequireEnumText(ref reader, "node kind") switch
        {
            "trigger" => GovernedLoopNodeKind.Trigger,
            "inference" => GovernedLoopNodeKind.Inference,
            "transform" => GovernedLoopNodeKind.Transform,
            "validate" => GovernedLoopNodeKind.Validate,
            "state" => GovernedLoopNodeKind.State,
            "condition" => GovernedLoopNodeKind.Condition,
            "join" => GovernedLoopNodeKind.Join,
            "wait" => GovernedLoopNodeKind.Wait,
            "action" => GovernedLoopNodeKind.Action,
            "humanReview" => GovernedLoopNodeKind.HumanReview,
            "humanInput" => GovernedLoopNodeKind.HumanInput,
            "childLoop" => GovernedLoopNodeKind.ChildLoop,
            "exit" => GovernedLoopNodeKind.Exit,
            "fail" => GovernedLoopNodeKind.Fail,
            _ => throw new JsonException("The governed-loop node kind is unsupported."),
        };

    private static string RequireEnumText(ref Utf8JsonReader reader, string description)
        => reader.TokenType == JsonTokenType.String
            ? reader.GetString() ?? throw new JsonException($"The governed-loop {description} is invalid.")
            : throw new JsonException($"The governed-loop {description} must be a canonical string.");

    private static void RequireSchema(ref Utf8JsonReader reader, string propertyName)
    {
        if (RequireInt32(ref reader, propertyName) != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            throw new JsonException("Only governed-loop execution schema version 1 is supported.");
        }
    }

    private static int RequireInt32(ref Utf8JsonReader reader, string propertyName)
    {
        RequireProperty(ref reader, propertyName);
        return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)
            ? value
            : throw new JsonException($"The governed-loop `{propertyName}` value is invalid.");
    }

    private static int? RequireNullableInt32(ref Utf8JsonReader reader, string propertyName)
    {
        RequireProperty(ref reader, propertyName);
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)
            ? value
            : throw new JsonException($"The governed-loop `{propertyName}` value is invalid.");
    }

    private static long RequireInt64(ref Utf8JsonReader reader, string propertyName)
    {
        RequireProperty(ref reader, propertyName);
        return reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value)
            ? value
            : throw new JsonException($"The governed-loop `{propertyName}` value is invalid.");
    }

    private static DateTimeOffset RequireDateTimeOffset(ref Utf8JsonReader reader, string propertyName)
    {
        RequireProperty(ref reader, propertyName);
        return reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var value)
            ? value
            : throw new JsonException($"The governed-loop `{propertyName}` timestamp is invalid.");
    }

    private static string RequireString(ref Utf8JsonReader reader, string propertyName)
    {
        RequireProperty(ref reader, propertyName);
        return reader.TokenType == JsonTokenType.String
            ? reader.GetString() ?? throw new JsonException($"The governed-loop `{propertyName}` value is invalid.")
            : throw new JsonException($"The governed-loop `{propertyName}` value must be a string.");
    }

    private static string? RequireNullableString(ref Utf8JsonReader reader, string propertyName)
    {
        RequireProperty(ref reader, propertyName);
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            _ => throw new JsonException($"The governed-loop `{propertyName}` value must be a string or null."),
        };
    }

    private static void RequireStartObject(ref Utf8JsonReader reader, string message)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(message);
        }
    }

    private static void RequireProperty(ref Utf8JsonReader reader, string propertyName)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(propertyName) || !reader.Read())
        {
            throw new JsonException($"The governed-loop `{propertyName}` property is missing or out of canonical order.");
        }
    }

    private static void RequireEndObject(ref Utf8JsonReader reader, string message)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException(message);
        }
    }
}
