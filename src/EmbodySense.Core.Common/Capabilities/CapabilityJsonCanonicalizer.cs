using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Common.Capabilities;

internal static class CapabilityJsonCanonicalizer
{
    internal static bool TryCanonicalizeSchema(string? json, out string? canonicalJson, out CapabilityContractError? error)
    {
        canonicalJson = null;
        if (string.IsNullOrEmpty(json) || json.Length > CapabilityContractLimits.MaxSchemaCharacters || !CapabilityTextRules.IsSafeNormalized(json, CapabilityContractLimits.MaxSchemaCharacters, allowEmpty: false))
        {
            error = new CapabilityContractError("invalid_json_schema", "$", "JSON schemas must be non-empty, bounded, normalized, and free of unsafe Unicode.");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = CapabilityContractLimits.MaxSchemaDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = new CapabilityContractError("invalid_json_schema", "$", "JSON schemas must have an object root.");
                return false;
            }

            if (!document.RootElement.TryGetProperty("$schema", out var dialect) || dialect.ValueKind != JsonValueKind.String || dialect.GetString() != CapabilityJsonSchema.Draft202012Dialect)
            {
                error = new CapabilityContractError("unsupported_json_schema_dialect", "$.$schema", $"Capability schemas must explicitly use {CapabilityJsonSchema.Draft202012Dialect}.");
                return false;
            }

            var elementCount = 0;
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                if (!TryWriteCanonical(writer, document.RootElement, "$", 1, ref elementCount, out error))
                {
                    return false;
                }

                writer.Flush();
            }

            canonicalJson = Encoding.UTF8.GetString(buffer.WrittenSpan);
            if (canonicalJson.Length > CapabilityContractLimits.MaxSchemaCharacters)
            {
                canonicalJson = null;
                error = new CapabilityContractError("json_schema_too_large", "$", "The canonical JSON schema exceeds the schema-1 character bound.");
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            error = new CapabilityContractError("invalid_json_schema", "$", $"The JSON schema is malformed: {exception.Message}");
            return false;
        }
    }

    private static bool TryWriteCanonical(Utf8JsonWriter writer, JsonElement element, string field, int depth, ref int elementCount, out CapabilityContractError? error)
    {
        if (depth > CapabilityContractLimits.MaxSchemaDepth || ++elementCount > CapabilityContractLimits.MaxSchemaElements)
        {
            error = new CapabilityContractError("json_schema_shape_exceeded", field, "The JSON schema exceeds the schema-1 depth or element-count bound.");
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return TryWriteObject(writer, element, field, depth, ref elementCount, out error);
            case JsonValueKind.Array:
                return TryWriteArray(writer, element, field, depth, ref elementCount, out error);
            case JsonValueKind.String:
                var value = element.GetString();
                if (!CapabilityTextRules.IsSafeNormalized(value, CapabilityContractLimits.MaxSchemaCharacters, allowEmpty: true))
                {
                    error = new CapabilityContractError("unsafe_json_schema_text", field, "JSON schema strings must be normalized and free of unsafe Unicode.");
                    return false;
                }

                writer.WriteStringValue(value);
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out var number) || !double.IsFinite(number) || IsNegativeZero(element.GetRawText()))
                {
                    error = new CapabilityContractError("unsafe_json_schema_number", field, "JSON schema numbers must be finite IEEE-754 values and cannot use negative zero.");
                    return false;
                }

                writer.WriteNumberValue(number);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                error = new CapabilityContractError("invalid_json_schema", field, "The JSON schema contains an unsupported JSON token.");
                return false;
        }

        error = null;
        return true;
    }

    private static bool TryWriteObject(Utf8JsonWriter writer, JsonElement element, string field, int depth, ref int elementCount, out CapabilityContractError? error)
    {
        var properties = element.EnumerateObject().ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!names.Add(property.Name))
            {
                error = new CapabilityContractError("duplicate_json_schema_property", $"{field}.{property.Name}", "JSON schema objects cannot contain duplicate property names.");
                return false;
            }

            if (!CapabilityTextRules.IsSafeNormalized(property.Name, 256, allowEmpty: true))
            {
                error = new CapabilityContractError("unsafe_json_schema_text", field, "JSON schema property names must be normalized and free of unsafe Unicode.");
                return false;
            }
        }

        writer.WriteStartObject();
        foreach (var property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            if (!TryWriteCanonical(writer, property.Value, $"{field}.{property.Name}", depth + 1, ref elementCount, out error))
            {
                return false;
            }
        }

        writer.WriteEndObject();
        error = null;
        return true;
    }

    private static bool TryWriteArray(Utf8JsonWriter writer, JsonElement element, string field, int depth, ref int elementCount, out CapabilityContractError? error)
    {
        writer.WriteStartArray();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryWriteCanonical(writer, item, $"{field}[{index}]", depth + 1, ref elementCount, out error))
            {
                return false;
            }

            index++;
        }

        writer.WriteEndArray();
        error = null;
        return true;
    }

    private static bool IsNegativeZero(string value)
    {
        return value.StartsWith('-') && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && number == 0d;
    }
}
