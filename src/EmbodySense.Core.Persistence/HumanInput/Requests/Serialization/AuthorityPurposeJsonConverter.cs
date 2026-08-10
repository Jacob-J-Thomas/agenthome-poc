using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;

internal sealed class AuthorityPurposeJsonConverter : JsonConverter<AuthorityPurpose>
{
    public override AuthorityPurpose Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!AuthorityPurpose.TryParse(value, out var purpose, out _))
        {
            throw new JsonException("The authority purpose is not canonical.");
        }

        return purpose!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityPurpose value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
