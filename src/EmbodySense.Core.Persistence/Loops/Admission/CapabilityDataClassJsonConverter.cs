using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class CapabilityDataClassJsonConverter : JsonConverter<CapabilityDataClass>
{
    public override CapabilityDataClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!CapabilityDataClass.TryParse(value, out var dataClass, out _))
        {
            throw new JsonException("The capability data class is not canonical.");
        }

        return dataClass!;
    }

    public override void Write(Utf8JsonWriter writer, CapabilityDataClass value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
