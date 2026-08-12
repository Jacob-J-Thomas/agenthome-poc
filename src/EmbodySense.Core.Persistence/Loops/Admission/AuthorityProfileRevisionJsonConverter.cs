using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class AuthorityProfileRevisionJsonConverter : JsonConverter<AuthorityProfileRevision>
{
    public override AuthorityProfileRevision Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!AuthorityProfileRevision.TryParse(value, out var revision, out _))
        {
            throw new JsonException("The authority profile revision is not canonical.");
        }

        return revision!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityProfileRevision value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
