using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

internal static class GovernedLoopTypedValueCanonicalizer
{
    public static bool TryCanonicalize(GovernedLoopValueKind kind, string? json, out string? canonicalJson, out GovernedLoopTypedValueError? error)
    {
        canonicalJson = null;
        if (kind is GovernedLoopValueKind.Unknown or GovernedLoopValueKind.Binary || !Enum.IsDefined(kind))
        {
            error = Error("typed-value.kind.unsupported", "$", "Pure-node values require one defined non-Binary portable kind.");
            return false;
        }

        if (string.IsNullOrEmpty(json) || json.Length > CustomLoopLimits.MaxGraphTypedValueUtf8Bytes || Encoding.UTF8.GetByteCount(json) > CustomLoopLimits.MaxGraphTypedValueUtf8Bytes)
        {
            error = Error("typed-value.json.invalid", "$", "Typed-value JSON must be non-empty and fit the schema-1 UTF-8 bound.");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = CustomLoopLimits.MaxGraphTypedValueDepth
            });
            if (!MatchesRootKind(kind, document.RootElement.ValueKind))
            {
                error = Error("typed-value.kind.mismatch", "$", "The JSON root does not match the declared portable value kind.");
                return false;
            }

            var elementCount = 0;
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                if (!TryWriteCanonical(writer, document.RootElement, kind == GovernedLoopValueKind.Integer, "$", 1, ref elementCount, out error))
                {
                    return false;
                }

                writer.Flush();
            }

            if (buffer.WrittenCount > CustomLoopLimits.MaxGraphTypedValueUtf8Bytes)
            {
                error = Error("typed-value.canonical-size.exceeded", "$", "The canonical typed value exceeds the schema-1 UTF-8 bound.");
                return false;
            }

            canonicalJson = Encoding.UTF8.GetString(buffer.WrittenSpan);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or EncoderFallbackException)
        {
            error = Error("typed-value.json.malformed", "$", "The typed-value JSON is malformed or exceeds its bounded parse shape.");
            return false;
        }
    }

    private static bool TryWriteCanonical(Utf8JsonWriter writer, JsonElement element, bool requireInteger, string path, int depth, ref int elementCount, out GovernedLoopTypedValueError? error)
    {
        if (depth > CustomLoopLimits.MaxGraphTypedValueDepth || ++elementCount > CustomLoopLimits.MaxGraphTypedValueElements)
        {
            error = Error("typed-value.shape.exceeded", path, "The typed value exceeds the schema-1 depth or element-count bound.");
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return TryWriteObject(writer, element, path, depth, ref elementCount, out error);
            case JsonValueKind.Array:
                return TryWriteArray(writer, element, path, depth, ref elementCount, out error);
            case JsonValueKind.String:
                var text = element.GetString();
                if (!GovernedLoopPureNodeTextRules.IsSafe(text, CustomLoopLimits.MaxGraphTypedValueStringCharacters))
                {
                    error = Error("typed-value.text.unsafe", path, "Typed-value text must be bounded, NFC-normalized, and free of unsafe Unicode.");
                    return false;
                }

                writer.WriteStringValue(text);
                break;
            case JsonValueKind.Number:
                if (requireInteger)
                {
                    return TryWriteInteger(writer, element, path, out error);
                }

                return TryWriteNumber(writer, element, path, out error);
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
                error = Error("typed-value.json.unsupported", path, "The typed value contains an unsupported JSON token.");
                return false;
        }

        error = null;
        return true;
    }

    private static bool TryWriteObject(Utf8JsonWriter writer, JsonElement element, string path, int depth, ref int elementCount, out GovernedLoopTypedValueError? error)
    {
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length > CustomLoopLimits.MaxGraphTypedValueCollectionEntries)
        {
            error = Error("typed-value.collection.exceeded", path, "A typed-value object exceeds the schema-1 entry bound.");
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!names.Add(property.Name))
            {
                error = Error("typed-value.object.duplicate-property", path, "Typed-value objects cannot contain duplicate property names.");
                return false;
            }

            if (!GovernedLoopPureNodeTextRules.IsSafe(property.Name, CustomLoopLimits.MaxGraphTypedValuePropertyNameCharacters))
            {
                error = Error("typed-value.property-name.unsafe", path, "Object property names must be bounded, NFC-normalized, and free of unsafe Unicode.");
                return false;
            }
        }

        writer.WriteStartObject();
        foreach (var property in properties.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            if (!TryWriteCanonical(writer, property.Value, requireInteger: false, ChildPath(path, property.Name), depth + 1, ref elementCount, out error))
            {
                return false;
            }
        }

        writer.WriteEndObject();
        error = null;
        return true;
    }

    private static bool TryWriteArray(Utf8JsonWriter writer, JsonElement element, string path, int depth, ref int elementCount, out GovernedLoopTypedValueError? error)
    {
        var items = element.EnumerateArray().ToArray();
        if (items.Length > CustomLoopLimits.MaxGraphTypedValueCollectionEntries)
        {
            error = Error("typed-value.collection.exceeded", path, "A typed-value array exceeds the schema-1 entry bound.");
            return false;
        }

        writer.WriteStartArray();
        for (var index = 0; index < items.Length; index++)
        {
            if (!TryWriteCanonical(writer, items[index], requireInteger: false, ChildPath(path, index.ToString(CultureInfo.InvariantCulture)), depth + 1, ref elementCount, out error))
            {
                return false;
            }
        }

        writer.WriteEndArray();
        error = null;
        return true;
    }

    private static bool TryWriteInteger(Utf8JsonWriter writer, JsonElement element, string path, out GovernedLoopTypedValueError? error)
    {
        var raw = element.GetRawText();
        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || !string.Equals(raw, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            error = Error("typed-value.integer.invalid", path, "Integer values must use the canonical signed 64-bit JSON integer form.");
            return false;
        }

        writer.WriteNumberValue(value);
        error = null;
        return true;
    }

    private static bool TryWriteNumber(Utf8JsonWriter writer, JsonElement element, string path, out GovernedLoopTypedValueError? error)
    {
        var raw = element.GetRawText();
        if (!GovernedLoopTypedValueNumberCanonicalizer.TryCanonicalize(raw, out var canonicalNumber, out var isNegativeZero)
            || isNegativeZero
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || !TryGetCanonicalFiniteNumber(value, out var finiteNumber)
            || !string.Equals(canonicalNumber, finiteNumber, StringComparison.Ordinal))
        {
            error = Error("typed-value.number.invalid", path, "Number values must round-trip exactly through finite IEEE-754 canonicalization and cannot use negative zero.");
            return false;
        }

        writer.WriteRawValue(canonicalNumber!, skipInputValidation: true);
        error = null;
        return true;
    }

    private static bool TryGetCanonicalFiniteNumber(double value, out string? canonicalNumber)
    {
        canonicalNumber = null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteNumberValue(value);
            writer.Flush();
        }

        return GovernedLoopTypedValueNumberCanonicalizer.TryCanonicalize(Encoding.UTF8.GetString(buffer.WrittenSpan), out canonicalNumber, out _);
    }

    private static bool MatchesRootKind(GovernedLoopValueKind kind, JsonValueKind valueKind)
    {
        if (valueKind == JsonValueKind.Null)
        {
            return true;
        }

        return kind switch
        {
            GovernedLoopValueKind.Text => valueKind == JsonValueKind.String,
            GovernedLoopValueKind.Boolean => valueKind is JsonValueKind.True or JsonValueKind.False,
            GovernedLoopValueKind.Integer or GovernedLoopValueKind.Number => valueKind == JsonValueKind.Number,
            GovernedLoopValueKind.Object => valueKind == JsonValueKind.Object,
            GovernedLoopValueKind.Array => valueKind == JsonValueKind.Array,
            _ => false
        };
    }

    private static string ChildPath(string parent, string segment)
    {
        var escaped = segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        var path = string.Concat(parent, "/", escaped);
        return path.Length <= CustomLoopLimits.MaxGraphValidationErrorPathCharacters ? path : "$";
    }

    private static GovernedLoopTypedValueError Error(string code, string path, string message) => GovernedLoopTypedValueError.Create(code, path, message);
}
