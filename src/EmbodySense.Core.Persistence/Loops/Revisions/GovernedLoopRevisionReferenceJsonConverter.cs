using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Persistence.Loops.Revisions;

internal sealed class GovernedLoopRevisionReferenceJsonConverter : JsonConverter<GovernedLoopRevisionReference>
{
    public override GovernedLoopRevisionReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A governed-loop revision reference must be an object.");
        }

        int? schemaVersion = null;
        string? graphId = null;
        string? revisionId = null;
        string? executableHash = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A governed-loop revision reference property name was expected.");
            }

            var name = reader.GetString() ?? throw new JsonException("A governed-loop revision reference property name is invalid.");
            if (!names.Add(name) || !reader.Read())
            {
                throw new JsonException("A governed-loop revision reference contains a duplicate or incomplete property.");
            }

            switch (name)
            {
                case "schemaVersion":
                    schemaVersion = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var parsedSchema) ? parsedSchema : throw new JsonException("The revision-reference schema version is invalid.");
                    break;
                case "graphId":
                    graphId = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("The revision-reference graph id is invalid.");
                    break;
                case "revisionId":
                    revisionId = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("The revision-reference revision id is invalid.");
                    break;
                case "executableHash":
                    executableHash = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("The revision-reference executable hash is invalid.");
                    break;
                default:
                    throw new JsonException("The revision reference contains an unsupported property.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || schemaVersion is null || graphId is null || revisionId is null || executableHash is null)
        {
            throw new JsonException("The revision reference is incomplete.");
        }

        try
        {
            return GovernedLoopRevisionReference.Create(schemaVersion.Value, graphId, revisionId, executableHash);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The revision reference is outside the schema-1 contract.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, GovernedLoopRevisionReference value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", value.SchemaVersion);
        writer.WriteString("graphId", value.GraphId);
        writer.WriteString("revisionId", value.RevisionId);
        writer.WriteString("executableHash", value.ExecutableHash);
        writer.WriteEndObject();
    }
}
