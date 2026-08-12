using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class AuthorityProfileHashJsonConverter : JsonConverter<AuthorityProfileHash>
{
    public override AuthorityProfileHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!AuthorityProfileHash.TryParse(value, out var hash, out _))
        {
            throw new JsonException("The authority profile hash is not canonical.");
        }

        return hash!;
    }

    public override void Write(Utf8JsonWriter writer, AuthorityProfileHash value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
