using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority.Grants;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;

internal sealed class AuthorityGrantRevisionJsonConverter : JsonConverter<AuthorityGrantRevision>
{
    public override AuthorityGrantRevision Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!AuthorityGrantRevision.TryParse(value, out var revision, out _))
        {
            throw new JsonException("The authority grant revision is not canonical.");
        }

        return revision!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityGrantRevision value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
