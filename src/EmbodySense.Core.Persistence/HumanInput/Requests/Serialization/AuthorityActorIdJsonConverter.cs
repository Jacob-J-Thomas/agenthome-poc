using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;

internal sealed class AuthorityActorIdJsonConverter : JsonConverter<AuthorityActorId>
{
    public override AuthorityActorId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!AuthorityActorId.TryParse(value, out var actorId, out _))
        {
            throw new JsonException("The authority actor identifier is not canonical.");
        }

        return actorId!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityActorId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
