using System.Buffers;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Creates and strictly reads canonical schema-1 typed-value envelopes.</summary>
public static class GovernedLoopTypedValueJson
{
    /// <summary>Canonicalizes a bounded value payload into one typed schema-1 envelope.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="kind">The exact non-Binary portable kind.</param>
    /// <param name="valueJson">The bounded JSON payload.</param>
    /// <param name="value">The immutable typed value on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when canonicalization succeeds.</returns>
    public static bool TryCreate(int schemaVersion, GovernedLoopValueKind kind, string? valueJson, out GovernedLoopTypedValue? value, out GovernedLoopTypedValueValidationResult validation)
    {
        value = null;
        if (schemaVersion != GovernedLoopTypedValue.CurrentSchemaVersion)
        {
            validation = Invalid("typed-value.schema-version.unsupported", "$", "Only typed-value schema version 1 is accepted; compatibility translation is not supported.");
            return false;
        }

        if (!GovernedLoopTypedValueCanonicalizer.TryCanonicalize(kind, valueJson, out var canonicalValueJson, out var error))
        {
            validation = new GovernedLoopTypedValueValidationResult([error!]);
            return false;
        }

        var canonicalJson = WriteEnvelope(kind, canonicalValueJson!);
        if (Encoding.UTF8.GetByteCount(canonicalJson) > CustomLoopLimits.MaxGraphTypedValueUtf8Bytes)
        {
            validation = Invalid("typed-value.canonical-size.exceeded", "$", "The canonical typed-value envelope exceeds the schema-1 UTF-8 bound.");
            return false;
        }

        value = new GovernedLoopTypedValue(kind, canonicalValueJson!, canonicalJson, GovernedLoopTypedValueHash.ComputeCanonical(canonicalJson));
        validation = Valid();
        return true;
    }

    /// <summary>Reads an exact canonical typed-value envelope and rejects equivalent noncanonical encodings.</summary>
    /// <param name="json">The candidate envelope.</param>
    /// <param name="value">The immutable typed value on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when the envelope is exact schema-1 canonical JSON.</returns>
    public static bool TryDeserialize(string? json, out GovernedLoopTypedValue? value, out GovernedLoopTypedValueValidationResult validation)
    {
        value = null;
        if (string.IsNullOrEmpty(json) || json.Length > CustomLoopLimits.MaxGraphTypedValueUtf8Bytes || Encoding.UTF8.GetByteCount(json) > CustomLoopLimits.MaxGraphTypedValueUtf8Bytes)
        {
            validation = Invalid("typed-value.document.invalid", "$", "A bounded canonical typed-value envelope is required.");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = CustomLoopLimits.MaxGraphTypedValueDepth + 1
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                validation = Invalid("typed-value.document.invalid", "$", "Typed-value envelopes require an object root.");
                return false;
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 3 || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 3)
            {
                validation = Invalid("typed-value.document.shape", "$", "Typed-value envelopes require exactly schemaVersion, kind, and value once each.");
                return false;
            }

            var byName = properties.ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            if (!byName.TryGetValue("schemaVersion", out var schemaVersionElement)
                || schemaVersionElement.ValueKind != JsonValueKind.Number
                || !schemaVersionElement.TryGetInt32(out var schemaVersion)
                || !byName.TryGetValue("kind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String
                || !GovernedLoopValueKindVocabulary.TryParse(kindElement.GetString(), out var kind)
                || !byName.TryGetValue("value", out var valueElement))
            {
                validation = Invalid("typed-value.document.shape", "$", "Typed-value envelope fields must use their exact schema-1 names and scalar shapes.");
                return false;
            }

            if (!TryCreate(schemaVersion, kind, valueElement.GetRawText(), out var candidate, out validation))
            {
                return false;
            }

            if (!string.Equals(json, candidate!.CanonicalJson, StringComparison.Ordinal))
            {
                validation = Invalid("typed-value.document.noncanonical", "$", "The typed-value envelope is valid only after normalization and is not accepted as durable canonical evidence.");
                return false;
            }

            value = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or EncoderFallbackException)
        {
            validation = Invalid("typed-value.document.malformed", "$", "The typed-value envelope is malformed or exceeds its bounded parse shape.");
            return false;
        }
    }

    private static string WriteEnvelope(GovernedLoopValueKind kind, string canonicalValueJson)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", GovernedLoopTypedValue.CurrentSchemaVersion);
            writer.WriteString("kind", GovernedLoopValueKindVocabulary.ToCanonical(kind));
            writer.WritePropertyName("value");
            writer.WriteRawValue(canonicalValueJson, skipInputValidation: true);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static GovernedLoopTypedValueValidationResult Valid() => new(Array.Empty<GovernedLoopTypedValueError>());

    private static GovernedLoopTypedValueValidationResult Invalid(string code, string path, string message) => new([GovernedLoopTypedValueError.Create(code, path, message)]);
}
