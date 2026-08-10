using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

internal static class HumanInputResponseCanonicalWriter
{
    internal static void WriteValue(Utf8JsonWriter writer, HumanInputResponseValue value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("kind", (int)value.Kind);
        WriteString(writer, "text", value.Text);
        WriteString(writer, "choiceId", value.ChoiceId);
        if (value.Confirmation is { } confirmation)
        {
            writer.WriteBoolean("confirmation", confirmation);
        }
        else
        {
            writer.WriteNull("confirmation");
        }

        writer.WritePropertyName("structuredFields");
        if (value.StructuredFields is not { } fields)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var field in fields.OrderBy(item => item?.FieldId, StringComparer.Ordinal))
            {
                if (field is null)
                {
                    writer.WriteNullValue();
                    continue;
                }

                writer.WriteStartObject();
                WriteString(writer, "fieldId", field.FieldId);
                WriteString(writer, "text", field.Text);
                WriteString(writer, "choiceId", field.ChoiceId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WritePropertyName("reference");
        if (value.Reference is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("kind", (int)value.Reference.Kind);
            WriteString(writer, "value", value.Reference.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    internal static void WriteRequestReference(Utf8JsonWriter writer, HumanInputRequestReference reference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", reference.SchemaVersion);
        WriteString(writer, "requestId", reference.RequestId);
        WriteString(writer, "requestVersionId", reference.RequestVersionId);
        WriteString(writer, "requestHash", reference.RequestHash);
        writer.WriteEndObject();
    }

    internal static void WriteBinding(Utf8JsonWriter writer, HumanInputRequestBinding binding)
    {
        writer.WriteStartObject();
        WriteString(writer, "workspaceId", binding.WorkspaceId);
        WriteString(writer, "loopGraphId", binding.LoopGraphId);
        WriteString(writer, "loopRevisionId", binding.LoopRevisionId);
        WriteString(writer, "nodeId", binding.NodeId);
        WriteString(writer, "runId", binding.RunId);
        WriteString(writer, "checkpointId", binding.CheckpointId);
        writer.WriteEndObject();
    }

    internal static void WriteResponseReference(Utf8JsonWriter writer, HumanInputResponseReference reference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", reference.SchemaVersion);
        WriteString(writer, "responseId", reference.ResponseId);
        writer.WritePropertyName("request");
        WriteRequestReference(writer, reference.Request);
        WriteString(writer, "valueHash", reference.ValueHash);
        WriteString(writer, "responseHash", reference.ResponseHash);
        writer.WriteEndObject();
    }

    internal static void WriteUtc(Utf8JsonWriter writer, string name, DateTimeOffset value)
        => writer.WriteString(name, value.ToString("O", CultureInfo.InvariantCulture));

    internal static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
