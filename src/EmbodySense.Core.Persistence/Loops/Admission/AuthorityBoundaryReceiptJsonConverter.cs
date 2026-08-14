using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class AuthorityBoundaryReceiptJsonConverter : JsonConverter<AuthorityBoundaryReceipt>
{
    public override AuthorityBoundaryReceipt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("An authority boundary receipt must be an object.");
        }

        int? schemaVersion = null;
        AuthorityBoundaryDecision? decision = null;
        IReadOnlyList<AuthorityBoundaryCondition>? conditions = null;
        IReadOnlyList<AuthorityProfileReference>? profiles = null;
        DateTimeOffset? evaluatedAtUtc = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("An authority boundary receipt property name was expected.");
            }

            var name = reader.GetString() ?? throw new JsonException("An authority boundary receipt property name is invalid.");
            if (!names.Add(name) || !reader.Read())
            {
                throw new JsonException("The authority boundary receipt contains a duplicate or incomplete property.");
            }

            switch (name)
            {
                case "schemaVersion":
                    schemaVersion = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var parsedSchema)
                        ? parsedSchema
                        : throw new JsonException("The authority boundary receipt schema version is invalid.");
                    break;
                case "decision":
                    decision = JsonSerializer.Deserialize<AuthorityBoundaryDecision>(ref reader, options);
                    break;
                case "conditions":
                    conditions = JsonSerializer.Deserialize<AuthorityBoundaryCondition[]>(ref reader, options);
                    break;
                case "profiles":
                    profiles = JsonSerializer.Deserialize<AuthorityProfileReference[]>(ref reader, options);
                    break;
                case "evaluatedAtUtc":
                    evaluatedAtUtc = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var parsedTime)
                        ? parsedTime
                        : throw new JsonException("The authority boundary receipt evaluation time is invalid.");
                    break;
                default:
                    throw new JsonException("The authority boundary receipt contains an unsupported property.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject
            || schemaVersion is null
            || decision is null
            || conditions is null
            || profiles is null
            || evaluatedAtUtc is null
            || !AuthorityBoundaryReceiptFactory.TryCreate(
                schemaVersion.Value,
                decision.Value,
                conditions,
                profiles,
                evaluatedAtUtc.Value,
                out var receipt,
                out _))
        {
            throw new JsonException("The authority boundary receipt is incomplete or outside the schema-1 contract.");
        }

        return receipt!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityBoundaryReceipt value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", value.SchemaVersion);
        writer.WritePropertyName("decision");
        JsonSerializer.Serialize(writer, value.Decision, options);
        writer.WritePropertyName("conditions");
        JsonSerializer.Serialize(writer, value.Conditions, options);
        writer.WritePropertyName("profiles");
        JsonSerializer.Serialize(writer, value.Profiles, options);
        writer.WriteString("evaluatedAtUtc", value.EvaluatedAtUtc);
        writer.WriteEndObject();
    }
}
