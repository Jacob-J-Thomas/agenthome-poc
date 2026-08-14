using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class GovernedLoopExecutionBindingJsonConverter : JsonConverter<GovernedLoopExecutionBinding>
{
    public override GovernedLoopExecutionBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A governed-loop execution binding must be an object.");
        }

        int? schemaVersion = null;
        string? runId = null;
        GovernedLoopRevisionReference? revision = null;
        long? generation = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A governed-loop execution-binding property name was expected.");
            }

            var name = reader.GetString() ?? throw new JsonException("An execution-binding property name is invalid.");
            if (!names.Add(name) || !reader.Read())
            {
                throw new JsonException("The execution binding contains a duplicate or incomplete property.");
            }

            switch (name)
            {
                case "schemaVersion":
                    schemaVersion = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var parsedSchema)
                        ? parsedSchema
                        : throw new JsonException("The execution-binding schema version is invalid.");
                    break;
                case "runId":
                    runId = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw new JsonException("The execution-binding run id is invalid.");
                    break;
                case "revision":
                    revision = JsonSerializer.Deserialize<GovernedLoopRevisionReference>(ref reader, options);
                    break;
                case "executionGeneration":
                    generation = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var parsedGeneration)
                        ? parsedGeneration
                        : throw new JsonException("The execution generation is invalid.");
                    break;
                default:
                    throw new JsonException("The execution binding contains an unsupported property.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || schemaVersion is null || runId is null || revision is null || generation is null)
        {
            throw new JsonException("The execution binding is incomplete.");
        }

        try
        {
            return GovernedLoopExecutionBinding.Create(schemaVersion.Value, runId, revision, generation.Value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The execution binding is outside the schema-1 contract.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, GovernedLoopExecutionBinding value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", value.SchemaVersion);
        writer.WriteString("runId", value.RunId);
        writer.WritePropertyName("revision");
        JsonSerializer.Serialize(writer, value.Revision, options);
        writer.WriteNumber("executionGeneration", value.ExecutionGeneration);
        writer.WriteEndObject();
    }
}
