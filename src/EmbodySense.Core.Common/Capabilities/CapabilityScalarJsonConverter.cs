using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

internal sealed class CapabilityScalarJsonConverter<T> : JsonConverter<T> where T : class
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Capability scalar `{typeToConvert.Name}` must be a canonical JSON string.");
        }

        var value = reader.GetString();
        object? parsed = typeToConvert == typeof(CapabilityId) && CapabilityId.TryParse(value, out var id, out _) ? id
            : typeToConvert == typeof(CapabilityProviderId) && CapabilityProviderId.TryParse(value, out var provider, out _) ? provider
            : typeToConvert == typeof(CapabilityVersion) && CapabilityVersion.TryParse(value, out var version, out _) ? version
            : typeToConvert == typeof(CapabilityVersionRange) && CapabilityVersionRange.TryParse(value, out var range, out _) ? range
            : typeToConvert == typeof(CapabilityDescriptorHash) && CapabilityDescriptorHash.TryParse(value, out var hash, out _) ? hash
            : typeToConvert == typeof(CapabilityIntegrityDigest) && CapabilityIntegrityDigest.TryParse(value, out var digest, out _) ? digest
            : null;
        return parsed is T typed ? typed : throw new JsonException($"Capability scalar `{typeToConvert.Name}` is not canonical.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var canonical = value switch
        {
            CapabilityId item => item.Value,
            CapabilityProviderId item => item.Value,
            CapabilityVersion item => item.Value,
            CapabilityVersionRange item => item.Value,
            CapabilityDescriptorHash item => item.Value,
            CapabilityIntegrityDigest item => item.Value,
            _ => throw new JsonException($"Capability scalar `{typeof(T).Name}` is not supported.")
        };
        writer.WriteStringValue(canonical);
    }
}
