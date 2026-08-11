using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority.Grants;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;

internal sealed class AuthorityGrantIdJsonConverter : JsonConverter<AuthorityGrantId>
{
    public override AuthorityGrantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!AuthorityGrantId.TryParse(value, out var grantId, out _))
        {
            throw new JsonException("The authority grant identifier is not canonical.");
        }

        return grantId!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityGrantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
