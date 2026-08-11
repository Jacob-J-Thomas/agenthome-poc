using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class AuthorityProfileIdJsonConverter : JsonConverter<AuthorityProfileId>
{
    public override AuthorityProfileId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!AuthorityProfileId.TryParse(value, out var profileId, out _))
        {
            throw new JsonException("The authority profile identifier is not canonical.");
        }

        return profileId!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityProfileId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
