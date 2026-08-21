using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Effects;

/// <summary>Bounds, validates against the authoritative capability schema, and canonicalizes structured actuator input.</summary>
public static class GovernedActuatorInputContract
{
    private const string Domain = "embodysense.governed-actuator-input.v1";

    /// <summary>Validates one JSON input against the supported deterministic subset of an exact capability schema.</summary>
    public static bool TryCreate(
        string? json,
        CapabilityJsonSchema? authoritativeSchema,
        out GovernedActuatorInputEvidence? evidence,
        out string? reasonCode)
    {
        evidence = null;
        reasonCode = "actuator-input-invalid";
        if (authoritativeSchema is null
            || !TryCanonicalize(json, out var canonical, out reasonCode))
        {
            return false;
        }

        try
        {
            using var input = JsonDocument.Parse(canonical!.CanonicalJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = GovernedLoopEffectAttemptContractLimits.MaxInputDepth,
            });
            using var schema = JsonDocument.Parse(authoritativeSchema.CanonicalJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = GovernedLoopEffectAttemptContractLimits.MaxInputDepth,
            });
            var elementCount = 0;
            if (!ValidateSchema(schema.RootElement, 1, ref elementCount, out reasonCode))
            {
                return false;
            }
            elementCount = 0;
            if (!ValidateAgainstSchema(input.RootElement, schema.RootElement, "$", 1, ref elementCount, out reasonCode))
            {
                return false;
            }

            evidence = canonical;
            reasonCode = null;
            return true;
        }
        catch (JsonException)
        {
            reasonCode = "actuator-input-malformed";
            return false;
        }
    }

    /// <summary>Bounds and losslessly canonicalizes structured JSON before any catalog dependency is consulted.</summary>
    public static bool TryCanonicalize(
        string? json,
        out GovernedActuatorInputEvidence? evidence,
        out string? reasonCode)
    {
        evidence = null;
        reasonCode = "actuator-input-invalid";
        if (string.IsNullOrEmpty(json)
            || Encoding.UTF8.GetByteCount(json) > GovernedLoopEffectAttemptContractLimits.MaxCanonicalInputUtf8Bytes)
        {
            return false;
        }

        try
        {
            using var input = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = GovernedLoopEffectAttemptContractLimits.MaxInputDepth,
            });
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                var elementCount = 0;
                if (!TryWriteCanonical(writer, input.RootElement, "$", 1, ref elementCount, out reasonCode))
                {
                    return false;
                }
                writer.Flush();
                if (buffer.WrittenCount > GovernedLoopEffectAttemptContractLimits.MaxCanonicalInputUtf8Bytes)
                {
                    reasonCode = "actuator-input-too-large";
                    return false;
                }
                evidence = new GovernedActuatorInputEvidence(
                    Encoding.UTF8.GetString(buffer.WrittenSpan),
                    Fingerprint(buffer.WrittenSpan),
                    buffer.WrittenCount,
                    elementCount);
            }
            reasonCode = null;
            return true;
        }
        catch (JsonException)
        {
            reasonCode = "actuator-input-malformed";
            return false;
        }
    }

    private static bool ValidateSchema(
        JsonElement schema,
        int depth,
        ref int elementCount,
        out string? reasonCode)
    {
        if (depth > GovernedLoopEffectAttemptContractLimits.MaxInputDepth
            || ++elementCount > GovernedLoopEffectAttemptContractLimits.MaxInputElements
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }
        var typeName = ReadJsonString(type);
        var allowed = AllowedKeywords(typeName, depth);
        if (allowed.Length == 0
            || schema.EnumerateObject().Any(property => !allowed.Contains(ReadJsonPropertyName(property), StringComparer.Ordinal))
            || depth == 1 && (!schema.TryGetProperty("$schema", out var dialect)
                || dialect.ValueKind != JsonValueKind.String
                || !string.Equals(ReadJsonString(dialect), CapabilityJsonSchema.Draft202012Dialect, StringComparison.Ordinal))
            || depth > 1 && schema.TryGetProperty("$schema", out _))
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (typeName != "object" || properties.ValueKind != JsonValueKind.Object)
            {
                reasonCode = "actuator-input-schema-unsupported";
                return false;
            }
            var children = properties.EnumerateObject().ToArray();
            if (children.Select(ReadJsonPropertyName).Distinct(StringComparer.Ordinal).Count() != children.Length)
            {
                reasonCode = "actuator-input-schema-unsupported";
                return false;
            }
            foreach (var child in children)
            {
                var childName = ReadJsonPropertyName(child);
                if (!CapabilityTextRules.IsSafeNormalized(childName, 256, allowEmpty: false))
                {
                    reasonCode = "actuator-input-schema-unsupported";
                    return false;
                }
                if (!ValidateSchema(child.Value, depth + 1, ref elementCount, out reasonCode))
                {
                    return false;
                }
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            if (typeName != "array")
            {
                reasonCode = "actuator-input-schema-unsupported";
                return false;
            }
            if (!ValidateSchema(items, depth + 1, ref elementCount, out reasonCode))
            {
                return false;
            }
        }
        if (schema.TryGetProperty("required", out var required))
        {
            var requiredItems = required.ValueKind == JsonValueKind.Array ? required.EnumerateArray().ToArray() : [];
            if (typeName != "object"
                || required.ValueKind != JsonValueKind.Array
                || requiredItems.Any(item => item.ValueKind != JsonValueKind.String)
                || requiredItems.Any(item => !CapabilityTextRules.IsSafeNormalized(ReadJsonString(item), 256, allowEmpty: false))
                || requiredItems.Select(ReadJsonString).Distinct(StringComparer.Ordinal).Count() != requiredItems.Length)
            {
                reasonCode = "actuator-input-schema-unsupported";
                return false;
            }
        }
        if (schema.TryGetProperty("additionalProperties", out var additional)
            && (typeName != "object" || additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }
        if (schema.TryGetProperty("maxItems", out var maxItems)
            && (typeName != "array" || !maxItems.TryGetInt32(out var itemMaximum) || itemMaximum < 0))
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }
        if (schema.TryGetProperty("maxLength", out var maxLength)
            && (typeName != "string" || !maxLength.TryGetInt32(out var lengthMaximum) || lengthMaximum < 0))
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }
        reasonCode = null;
        return true;
    }

    private static bool ValidateAgainstSchema(
        JsonElement value,
        JsonElement schema,
        string path,
        int depth,
        ref int elementCount,
        out string? reasonCode)
    {
        if (depth > GovernedLoopEffectAttemptContractLimits.MaxInputDepth
            || ++elementCount > GovernedLoopEffectAttemptContractLimits.MaxInputElements
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }

        var typeName = ReadJsonString(type);
        var allowedKeywords = AllowedKeywords(typeName, depth);
        if (allowedKeywords.Length == 0
            || schema.EnumerateObject().Any(property => !allowedKeywords.Contains(ReadJsonPropertyName(property), StringComparer.Ordinal))
            || depth == 1 && (!schema.TryGetProperty("$schema", out var dialect)
                || dialect.ValueKind != JsonValueKind.String
                || !string.Equals(ReadJsonString(dialect), CapabilityJsonSchema.Draft202012Dialect, StringComparison.Ordinal))
            || depth > 1 && schema.TryGetProperty("$schema", out _))
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }

        if (!TypeMatches(value, typeName))
        {
            reasonCode = "actuator-input-schema-mismatch";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return ValidateObject(value, schema, path, depth, ref elementCount, out reasonCode);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            return ValidateArray(value, schema, path, depth, ref elementCount, out reasonCode);
        }
        if (value.ValueKind == JsonValueKind.String
            && (!CapabilityTextRules.IsSafeNormalized(ReadJsonString(value), GovernedLoopEffectAttemptContractLimits.MaxCanonicalInputUtf8Bytes, allowEmpty: true)
                || schema.TryGetProperty("maxLength", out var maxLength)
                    && (!maxLength.TryGetInt32(out var maximum) || maximum < 0 || ReadJsonString(value)!.Length > maximum)))
        {
            reasonCode = "actuator-input-schema-mismatch";
            return false;
        }
        if (value.ValueKind == JsonValueKind.Number && !TryCanonicalNumber(value, out _))
        {
            reasonCode = "actuator-input-number-invalid";
            return false;
        }

        reasonCode = null;
        return true;
    }

    private static string[] AllowedKeywords(string? type, int depth)
        => type switch
        {
            "object" => depth == 1
                ? ["$schema", "type", "properties", "required", "additionalProperties"]
                : ["type", "properties", "required", "additionalProperties"],
            "array" => depth == 1
                ? ["$schema", "type", "items", "maxItems"]
                : ["type", "items", "maxItems"],
            "string" => depth == 1
                ? ["$schema", "type", "maxLength"]
                : ["type", "maxLength"],
            "integer" or "number" or "boolean" or "null" => depth == 1
                ? ["$schema", "type"]
                : ["type"],
            _ => [],
        };

    private static bool ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        int depth,
        ref int elementCount,
        out string? reasonCode)
    {
        var properties = value.EnumerateObject().ToArray();
        if (properties.Select(ReadJsonPropertyName).Distinct(StringComparer.Ordinal).Count() != properties.Length)
        {
            reasonCode = "actuator-input-duplicate-property";
            return false;
        }
        if (!properties.All(property => CapabilityTextRules.IsSafeNormalized(ReadJsonPropertyName(property), 256, allowEmpty: false)))
        {
            reasonCode = "actuator-input-text-invalid";
            return false;
        }

        var schemaProperties = schema.TryGetProperty("properties", out var declared)
            ? declared
            : default;
        if (schemaProperties.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }
        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array)
            {
                reasonCode = "actuator-input-schema-unsupported";
                return false;
            }
            var requiredNames = required.EnumerateArray().ToArray();
            if (requiredNames.Any(item => item.ValueKind != JsonValueKind.String)
                || requiredNames.Select(ReadJsonString).Distinct(StringComparer.Ordinal).Count() != requiredNames.Length)
            {
                reasonCode = "actuator-input-schema-unsupported";
                return false;
            }
            foreach (var item in requiredNames)
            {
                if (!value.TryGetProperty(ReadJsonString(item)!, out _))
                {
                    reasonCode = "actuator-input-required-property-missing";
                    return false;
                }
            }
        }

        var permitsAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind == JsonValueKind.True;
        if (schema.TryGetProperty("additionalProperties", out additional)
            && additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            reasonCode = "actuator-input-schema-unsupported";
            return false;
        }
        foreach (var property in properties)
        {
            var propertyName = ReadJsonPropertyName(property);
            if (schemaProperties.ValueKind == JsonValueKind.Object && schemaProperties.TryGetProperty(propertyName, out var propertySchema))
            {
                if (!ValidateAgainstSchema(property.Value, propertySchema, $"{path}.{propertyName}", depth + 1, ref elementCount, out reasonCode))
                {
                    return false;
                }
            }
            else if (!permitsAdditional)
            {
                reasonCode = "actuator-input-additional-property";
                return false;
            }
            else if (!CountUntyped(property.Value, depth + 1, ref elementCount, out reasonCode))
            {
                return false;
            }
        }

        reasonCode = null;
        return true;
    }

    private static bool ValidateArray(
        JsonElement value,
        JsonElement schema,
        string path,
        int depth,
        ref int elementCount,
        out string? reasonCode)
    {
        var items = value.EnumerateArray().ToArray();
        if (schema.TryGetProperty("maxItems", out var maxItems)
            && (!maxItems.TryGetInt32(out var maximum) || maximum < 0 || items.Length > maximum))
        {
            reasonCode = "actuator-input-schema-mismatch";
            return false;
        }
        if (!schema.TryGetProperty("items", out var itemSchema))
        {
            foreach (var item in items)
            {
                if (!CountUntyped(item, depth + 1, ref elementCount, out reasonCode))
                {
                    return false;
                }
            }
            reasonCode = null;
            return true;
        }
        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            if (!ValidateAgainstSchema(item, itemSchema, $"{path}[{index}]", depth + 1, ref elementCount, out reasonCode))
            {
                return false;
            }
        }
        reasonCode = null;
        return true;
    }

    private static bool CountUntyped(JsonElement value, int depth, ref int elementCount, out string? reasonCode)
    {
        if (depth > GovernedLoopEffectAttemptContractLimits.MaxInputDepth
            || ++elementCount > GovernedLoopEffectAttemptContractLimits.MaxInputElements)
        {
            reasonCode = "actuator-input-shape-exceeded";
            return false;
        }
        if (value.ValueKind == JsonValueKind.String
            && !CapabilityTextRules.IsSafeNormalized(ReadJsonString(value), GovernedLoopEffectAttemptContractLimits.MaxCanonicalInputUtf8Bytes, allowEmpty: true))
        {
            reasonCode = "actuator-input-text-invalid";
            return false;
        }
        if (value.ValueKind == JsonValueKind.Number && !TryCanonicalNumber(value, out _))
        {
            reasonCode = "actuator-input-number-invalid";
            return false;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Select(ReadJsonPropertyName).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            {
                reasonCode = "actuator-input-duplicate-property";
                return false;
            }
            foreach (var property in properties)
            {
                if (!CapabilityTextRules.IsSafeNormalized(ReadJsonPropertyName(property), 256, allowEmpty: false))
                {
                    reasonCode = "actuator-input-text-invalid";
                    return false;
                }
                if (!CountUntyped(property.Value, depth + 1, ref elementCount, out reasonCode))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (!CountUntyped(item, depth + 1, ref elementCount, out reasonCode))
                {
                    return false;
                }
            }
        }
        reasonCode = null;
        return true;
    }

    private static bool TryWriteCanonical(
        Utf8JsonWriter writer,
        JsonElement value,
        string path,
        int depth,
        ref int elementCount,
        out string? reasonCode)
    {
        if (depth > GovernedLoopEffectAttemptContractLimits.MaxInputDepth
            || ++elementCount > GovernedLoopEffectAttemptContractLimits.MaxInputElements)
        {
            reasonCode = "actuator-input-shape-exceeded";
            return false;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Select(ReadJsonPropertyName).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    reasonCode = "actuator-input-duplicate-property";
                    return false;
                }
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(ReadJsonPropertyName, StringComparer.Ordinal))
                {
                    var propertyName = ReadJsonPropertyName(property);
                    writer.WritePropertyName(propertyName);
                    if (!TryWriteCanonical(writer, property.Value, $"{path}.{propertyName}", depth + 1, ref elementCount, out reasonCode))
                    {
                        return false;
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryWriteCanonical(writer, item, $"{path}[{index++}]", depth + 1, ref elementCount, out reasonCode))
                    {
                        return false;
                    }
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(ReadJsonString(value));
                break;
            case JsonValueKind.Number:
                if (!TryCanonicalNumber(value, out var number))
                {
                    reasonCode = "actuator-input-number-invalid";
                    return false;
                }
                writer.WriteRawValue(number!, skipInputValidation: true);
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
                reasonCode = "actuator-input-token-invalid";
                return false;
        }
        reasonCode = null;
        return true;
    }

    private static bool TryCanonicalNumber(JsonElement value, out string? canonical)
    {
        canonical = null;
        var raw = value.GetRawText();
        var negative = raw.StartsWith("-", StringComparison.Ordinal);
        var unsigned = negative ? raw[1..] : raw;
        var exponentSeparator = unsigned.IndexOfAny(['e', 'E']);
        var significand = exponentSeparator < 0 ? unsigned : unsigned[..exponentSeparator];
        var exponentText = exponentSeparator < 0 ? null : unsigned[(exponentSeparator + 1)..];
        var dot = significand.IndexOf('.');
        var integer = dot < 0 ? significand : significand[..dot];
        var fraction = dot < 0 ? string.Empty : significand[(dot + 1)..];
        var digits = integer + fraction;
        var explicitExponent = 0;
        if (digits.Length is < 1 or > GovernedLoopEffectAttemptContractLimits.MaxInputNumberDigits
            || digits.Any(character => character is < '0' or > '9')
            || exponentText is not null && !TryParseExponent(exponentText, out explicitExponent))
        {
            return false;
        }
        var firstNonZero = 0;
        while (firstNonZero < digits.Length && digits[firstNonZero] == '0')
        {
            firstNonZero++;
        }
        if (firstNonZero == digits.Length)
        {
            if (negative)
            {
                return false;
            }
            canonical = "0";
            return true;
        }
        digits = digits[firstNonZero..];
        var exponent = checked(explicitExponent - fraction.Length);
        while (digits.Length > 1 && digits[^1] == '0')
        {
            digits = digits[..^1];
            exponent = checked(exponent + 1);
        }
        if (Math.Abs((long)exponent) > GovernedLoopEffectAttemptContractLimits.MaxInputNumberExponent)
        {
            return false;
        }
        canonical = (negative ? "-" : string.Empty) + digits + (exponent == 0 ? string.Empty : $"e{exponent}");
        return true;
    }

    private static bool TryParseExponent(string value, out int exponent)
    {
        exponent = 0;
        var index = value.Length > 0 && value[0] is '+' or '-' ? 1 : 0;
        if (index == value.Length || value.Length - index > 5 || value[index..].Any(character => character is < '0' or > '9'))
        {
            return false;
        }
        var negative = index == 1 && value[0] == '-';
        foreach (var character in value[index..])
        {
            exponent = checked(exponent * 10 + character - '0');
            if (exponent > GovernedLoopEffectAttemptContractLimits.MaxInputNumberExponent)
            {
                return false;
            }
        }
        exponent = negative ? -exponent : exponent;
        return true;
    }

    private static bool TypeMatches(JsonElement value, string? type)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => IsInteger(value),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };

    private static string? ReadJsonString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("JSON text contains malformed UTF-16.", exception);
        }
    }

    private static string ReadJsonPropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("JSON text contains malformed UTF-16.", exception);
        }
    }

    private static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !TryCanonicalNumber(value, out var canonical))
        {
            return false;
        }
        var exponentSeparator = canonical!.IndexOf('e');
        return exponentSeparator < 0 || int.Parse(canonical[(exponentSeparator + 1)..], System.Globalization.CultureInfo.InvariantCulture) >= 0;
    }

    private static string Fingerprint(ReadOnlySpan<byte> canonicalUtf8)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var domain = Encoding.UTF8.GetBytes(Domain);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, domain.Length);
        hash.AppendData(length);
        hash.AppendData(domain);
        BinaryPrimitives.WriteInt32BigEndian(length, canonicalUtf8.Length);
        hash.AppendData(length);
        hash.AppendData(canonicalUtf8);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
