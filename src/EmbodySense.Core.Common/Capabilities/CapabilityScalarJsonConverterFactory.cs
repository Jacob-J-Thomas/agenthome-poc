using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>Persists canonical capability scalar value objects as their strict string forms.</summary>
public sealed class CapabilityScalarJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(CapabilityId)
            || typeToConvert == typeof(CapabilityProviderId)
            || typeToConvert == typeof(CapabilityVersion)
            || typeToConvert == typeof(CapabilityVersionRange)
            || typeToConvert == typeof(CapabilityDescriptorHash)
            || typeToConvert == typeof(CapabilityIntegrityDigest);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (!CanConvert(typeToConvert))
        {
            throw new NotSupportedException($"Capability scalar type `{typeToConvert.Name}` is not supported.");
        }

        return (JsonConverter)Activator.CreateInstance(typeof(CapabilityScalarJsonConverter<>).MakeGenericType(typeToConvert))!;
    }
}
