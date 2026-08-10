using EmbodySense.Core.Common.Capabilities.Models;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Serializes and parses the closed, canonical schema-version-1 capability descriptor JSON contract.
/// </summary>
public static class CapabilityDescriptorJson
{
    private static readonly string[] _rootProperties = ["compatibility", "id", "implementation", "inputSchema", "kind", "outputSchema", "provenance", "purpose", "requirements", "resourceLimits", "schemaVersion", "sideEffectClass", "version"];

    /// <summary>
    /// Serializes a valid descriptor to deterministic compact JSON.
    /// </summary>
    /// <param name="descriptor">The descriptor to serialize.</param>
    /// <param name="json">The canonical JSON when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when serialization succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TrySerialize(CapabilityDescriptor? descriptor, out string? json, out CapabilityContractValidationResult validation)
    {
        validation = CapabilityDescriptorValidator.Validate(descriptor);
        if (!validation.IsValid)
        {
            json = null;
            return false;
        }

        var value = descriptor!;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteCompatibility(writer, value.Compatibility);
            writer.WriteString("id", value.Id.Value);
            WriteImplementation(writer, value.Implementation);
            writer.WritePropertyName("inputSchema");
            writer.WriteRawValue(value.InputSchema.CanonicalJson, skipInputValidation: true);
            writer.WriteString("kind", CapabilityContractVocabulary.ToCanonical(value.Kind));
            writer.WritePropertyName("outputSchema");
            writer.WriteRawValue(value.OutputSchema.CanonicalJson, skipInputValidation: true);
            WriteProvenance(writer, value.Provenance);
            writer.WriteString("purpose", value.Purpose);
            WriteRequirements(writer, value.Requirements);
            WriteResourceLimits(writer, value.ResourceLimits);
            writer.WriteNumber("schemaVersion", value.SchemaVersion);
            writer.WriteString("sideEffectClass", CapabilityContractVocabulary.ToCanonical(value.SideEffectClass));
            writer.WriteString("version", value.Version.Value);
            writer.WriteEndObject();
            writer.Flush();
        }

        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (json.Length <= CapabilityContractLimits.MaxDescriptorJsonCharacters)
        {
            return true;
        }

        json = null;
        validation = new CapabilityContractValidationResult([new CapabilityContractError("descriptor_json_too_large", "$", "The canonical descriptor exceeds the schema-1 size bound.")]);
        return false;
    }

    /// <summary>
    /// Parses closed descriptor JSON, rejecting unknown fields including dedicated authority, trust, configuration, or secret-value properties. Bounded free-form text and JSON Schema annotations remain untrusted content rather than a secret-free projection.
    /// </summary>
    /// <param name="json">The candidate descriptor JSON.</param>
    /// <param name="descriptor">The parsed descriptor when successful.</param>
    /// <param name="validation">The structured parse and validation result.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryDeserialize(string? json, out CapabilityDescriptor? descriptor, out CapabilityContractValidationResult validation)
    {
        descriptor = null;
        var errors = new List<CapabilityContractError>();
        if (string.IsNullOrEmpty(json) || json.Length > CapabilityContractLimits.MaxDescriptorJsonCharacters || !CapabilityTextRules.IsSafeNormalized(json, CapabilityContractLimits.MaxDescriptorJsonCharacters, allowEmpty: false))
        {
            validation = Invalid("invalid_descriptor_json", "$", "Descriptor JSON must be non-empty, bounded, normalized, and free of unsafe Unicode.");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            var root = document.RootElement;
            if (!ValidateObjectShape(root, "$", _rootProperties, errors))
            {
                validation = new CapabilityContractValidationResult(errors);
                return false;
            }

            var schemaVersion = ReadInt32(root, "schemaVersion", "schemaVersion", errors);
            var id = ParseId(root, errors);
            var kind = ParseKind(root, errors);
            var version = ParseVersion(root, errors);
            var implementation = ParseImplementation(root, errors);
            var provenance = ParseProvenance(root, errors);
            var compatibility = ParseCompatibility(root, errors);
            var purpose = ReadString(root, "purpose", "purpose", errors);
            var inputSchema = ParseSchema(root, "inputSchema", errors);
            var outputSchema = ParseSchema(root, "outputSchema", errors);
            var limits = ParseResourceLimits(root, errors);
            var sideEffect = ParseSideEffect(root, errors);
            var requirements = ParseRequirements(root, errors);
            if (errors.Count > 0)
            {
                validation = new CapabilityContractValidationResult(errors);
                return false;
            }

            descriptor = new CapabilityDescriptor(schemaVersion, id!, kind, version!, implementation!, provenance!, compatibility!, purpose!, inputSchema!, outputSchema!, limits!, sideEffect, requirements!);
            validation = CapabilityDescriptorValidator.Validate(descriptor);
            if (!validation.IsValid)
            {
                descriptor = null;
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            validation = Invalid("invalid_descriptor_json", "$", $"The descriptor JSON is malformed: {exception.Message}");
            return false;
        }
    }

    private static void WriteCompatibility(Utf8JsonWriter writer, CapabilityCompatibility compatibility)
    {
        writer.WritePropertyName("compatibility");
        writer.WriteStartObject();
        writer.WriteString("hostVersionRange", compatibility.HostVersionRange.Value);
        writer.WritePropertyName("supportedPlatforms");
        writer.WriteStartArray();
        foreach (var platform in compatibility.SupportedPlatforms.OrderBy(item => item))
        {
            writer.WriteStringValue(platform.ToString());
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteImplementation(Utf8JsonWriter writer, CapabilityImplementationIdentity implementation)
    {
        writer.WritePropertyName("implementation");
        writer.WriteStartObject();
        writer.WriteString("implementationId", implementation.ImplementationId);
        writer.WriteString("providerId", implementation.ProviderId.Value);
        writer.WriteEndObject();
    }

    private static void WriteProvenance(Utf8JsonWriter writer, CapabilityProvenance provenance)
    {
        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        writer.WriteString("integrity", provenance.Integrity?.Value);
        writer.WriteString("kind", CapabilityContractVocabulary.ToCanonical(provenance.Kind));
        writer.WriteString("sourceRevision", provenance.SourceRevision);
        writer.WriteString("sourceUri", provenance.SourceUri);
        writer.WriteEndObject();
    }

    private static void WriteRequirements(Utf8JsonWriter writer, CapabilityAccessRequirements requirements)
    {
        writer.WritePropertyName("requirements");
        writer.WriteStartObject();
        WriteStringArray(writer, "dataClasses", requirements.DataClasses.Select(item => item.Value));
        WriteStringArray(writer, "egressDestinations", requirements.EgressDestinations);
        writer.WriteString("egressMode", CapabilityContractVocabulary.ToCanonical(requirements.EgressMode));
        WriteStringArray(writer, "secrets", requirements.Secrets.Select(item => item.Name));
        writer.WriteEndObject();
    }

    private static void WriteResourceLimits(Utf8JsonWriter writer, CapabilityResourceLimits limits)
    {
        writer.WritePropertyName("resourceLimits");
        writer.WriteStartObject();
        writer.WriteNumber("maxConcurrency", limits.MaxConcurrency);
        writer.WriteNumber("maxExecutionMilliseconds", limits.MaxExecutionMilliseconds);
        writer.WriteNumber("maxMemoryBytes", limits.MaxMemoryBytes);
        writer.WriteNumber("maxOutputBytes", limits.MaxOutputBytes);
        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static CapabilityId? ParseId(JsonElement root, List<CapabilityContractError> errors)
    {
        var value = ReadString(root, "id", "id", errors);
        CapabilityId? id = null;
        if (value is null || CapabilityId.TryParse(value, out id, out _))
        {
            return id;
        }

        Add(errors, "invalid_capability_id", "id", "The descriptor capability id is not canonical.");
        return null;
    }

    private static CapabilityKind ParseKind(JsonElement root, List<CapabilityContractError> errors)
    {
        var value = ReadString(root, "kind", "kind", errors);
        if (value is not null && CapabilityContractVocabulary.TryParse(value, out CapabilityKind kind))
        {
            return kind;
        }

        Add(errors, "unsupported_capability_kind", "kind", "The descriptor capability kind is absent or unsupported.");
        return CapabilityKind.Unknown;
    }

    private static CapabilityVersion? ParseVersion(JsonElement root, List<CapabilityContractError> errors)
    {
        var value = ReadString(root, "version", "version", errors);
        CapabilityVersion? version = null;
        if (value is null || CapabilityVersion.TryParse(value, out version, out _))
        {
            return version;
        }

        Add(errors, "invalid_capability_version", "version", "The descriptor exact version is not canonical SemVer 2.0.0.");
        return null;
    }

    private static CapabilityImplementationIdentity? ParseImplementation(JsonElement root, List<CapabilityContractError> errors)
    {
        var element = root.GetProperty("implementation");
        if (!ValidateObjectShape(element, "implementation", ["implementationId", "providerId"], errors))
        {
            return null;
        }

        var providerText = ReadString(element, "providerId", "implementation.providerId", errors);
        var implementationId = ReadString(element, "implementationId", "implementation.implementationId", errors);
        CapabilityProviderId? provider = null;
        if (providerText is null || !CapabilityProviderId.TryParse(providerText, out provider, out _))
        {
            Add(errors, "invalid_provider_id", "implementation.providerId", "The implementation provider id is not canonical.");
        }

        return provider is null || implementationId is null ? null : new CapabilityImplementationIdentity(provider, implementationId);
    }

    private static CapabilityProvenance? ParseProvenance(JsonElement root, List<CapabilityContractError> errors)
    {
        var element = root.GetProperty("provenance");
        if (!ValidateObjectShape(element, "provenance", ["integrity", "kind", "sourceRevision", "sourceUri"], errors))
        {
            return null;
        }

        var kindText = ReadString(element, "kind", "provenance.kind", errors);
        var kind = CapabilityProvenanceKind.Unknown;
        if (kindText is null || !CapabilityContractVocabulary.TryParse(kindText, out kind))
        {
            Add(errors, "unsupported_provenance_kind", "provenance.kind", "The provenance kind is absent or unsupported.");
        }

        var sourceUri = ReadString(element, "sourceUri", "provenance.sourceUri", errors);
        var revision = ReadNullableString(element, "sourceRevision", "provenance.sourceRevision", errors);
        var integrityText = ReadNullableString(element, "integrity", "provenance.integrity", errors);
        CapabilityIntegrityDigest? integrity = null;
        if (integrityText is not null && !CapabilityIntegrityDigest.TryParse(integrityText, out integrity, out _))
        {
            Add(errors, "invalid_integrity_digest", "provenance.integrity", "The provenance integrity digest is not canonical.");
        }

        return sourceUri is null ? null : new CapabilityProvenance(kind, sourceUri, revision, integrity);
    }

    private static CapabilityCompatibility? ParseCompatibility(JsonElement root, List<CapabilityContractError> errors)
    {
        var element = root.GetProperty("compatibility");
        if (!ValidateObjectShape(element, "compatibility", ["hostVersionRange", "supportedPlatforms"], errors))
        {
            return null;
        }

        var rangeText = ReadString(element, "hostVersionRange", "compatibility.hostVersionRange", errors);
        CapabilityVersionRange? range = null;
        if (rangeText is null || !CapabilityVersionRange.TryParse(rangeText, out range, out _))
        {
            Add(errors, "invalid_capability_version_range", "compatibility.hostVersionRange", "The host compatible-version range is not canonical.");
        }

        var platforms = new List<CapabilityPlatform>();
        if (TryGetBoundedArray(element, "supportedPlatforms", "compatibility.supportedPlatforms", CapabilityContractLimits.MaxPlatforms, allowEmpty: false, errors, out var array))
        {
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !CapabilityPlatform.TryParse(item.GetString(), out var platform, out _))
                {
                    Add(errors, "invalid_capability_platform", $"compatibility.supportedPlatforms[{index}]", "The supported platform is not canonical.");
                }
                else
                {
                    platforms.Add(platform!);
                }

                index++;
            }
        }

        return range is null ? null : new CapabilityCompatibility(range, platforms);
    }

    private static CapabilityJsonSchema? ParseSchema(JsonElement root, string propertyName, List<CapabilityContractError> errors)
    {
        var element = root.GetProperty(propertyName);
        if (!CapabilityJsonSchema.TryCreate(element.GetRawText(), out var schema, out var error))
        {
            Add(errors, error?.Code ?? "invalid_json_schema", propertyName, error?.Message ?? "The JSON schema is invalid.");
        }

        return schema;
    }

    private static CapabilityResourceLimits? ParseResourceLimits(JsonElement root, List<CapabilityContractError> errors)
    {
        var element = root.GetProperty("resourceLimits");
        if (!ValidateObjectShape(element, "resourceLimits", ["maxConcurrency", "maxExecutionMilliseconds", "maxMemoryBytes", "maxOutputBytes"], errors))
        {
            return null;
        }

        return new CapabilityResourceLimits(
            ReadInt32(element, "maxExecutionMilliseconds", "resourceLimits.maxExecutionMilliseconds", errors),
            ReadInt64(element, "maxMemoryBytes", "resourceLimits.maxMemoryBytes", errors),
            ReadInt32(element, "maxOutputBytes", "resourceLimits.maxOutputBytes", errors),
            ReadInt32(element, "maxConcurrency", "resourceLimits.maxConcurrency", errors));
    }

    private static CapabilitySideEffectClass ParseSideEffect(JsonElement root, List<CapabilityContractError> errors)
    {
        var value = ReadString(root, "sideEffectClass", "sideEffectClass", errors);
        if (value is not null && CapabilityContractVocabulary.TryParse(value, out CapabilitySideEffectClass sideEffect))
        {
            return sideEffect;
        }

        Add(errors, "unsupported_side_effect_class", "sideEffectClass", "The side-effect class is absent or unsupported.");
        return CapabilitySideEffectClass.Unknown;
    }

    private static CapabilityAccessRequirements? ParseRequirements(JsonElement root, List<CapabilityContractError> errors)
    {
        var element = root.GetProperty("requirements");
        if (!ValidateObjectShape(element, "requirements", ["dataClasses", "egressDestinations", "egressMode", "secrets"], errors))
        {
            return null;
        }

        var dataClasses = new List<CapabilityDataClass>();
        if (TryGetBoundedArray(element, "dataClasses", "requirements.dataClasses", CapabilityContractLimits.MaxDataClasses, allowEmpty: true, errors, out var dataArray))
        {
            ParseDataClasses(dataArray, dataClasses, errors);
        }

        var destinations = new List<string>();
        if (TryGetBoundedArray(element, "egressDestinations", "requirements.egressDestinations", CapabilityContractLimits.MaxEgressDestinations, allowEmpty: true, errors, out var destinationArray))
        {
            ParseStrings(destinationArray, "requirements.egressDestinations", destinations, errors);
        }

        var modeText = ReadString(element, "egressMode", "requirements.egressMode", errors);
        var mode = CapabilityEgressMode.Unknown;
        if (modeText is null || !CapabilityContractVocabulary.TryParse(modeText, out mode))
        {
            Add(errors, "unsupported_egress_mode", "requirements.egressMode", "The egress mode is absent or unsupported.");
        }

        var secrets = new List<CapabilitySecretRequirement>();
        if (TryGetBoundedArray(element, "secrets", "requirements.secrets", CapabilityContractLimits.MaxSecretRequirements, allowEmpty: true, errors, out var secretArray))
        {
            ParseSecrets(secretArray, secrets, errors);
        }

        return new CapabilityAccessRequirements(dataClasses, mode, destinations, secrets);
    }

    private static void ParseDataClasses(JsonElement array, List<CapabilityDataClass> values, List<CapabilityContractError> errors)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !CapabilityDataClass.TryParse(item.GetString(), out var value, out _))
            {
                Add(errors, "invalid_data_class", $"requirements.dataClasses[{index}]", "The data class is not canonical.");
            }
            else
            {
                values.Add(value!);
            }

            index++;
        }
    }

    private static void ParseSecrets(JsonElement array, List<CapabilitySecretRequirement> values, List<CapabilityContractError> errors)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !CapabilitySecretRequirement.TryParse(item.GetString(), out var value, out _))
            {
                Add(errors, "invalid_secret_requirement", $"requirements.secrets[{index}]", "The secret requirement must be a canonical reference name, never a secret value.");
            }
            else
            {
                values.Add(value!);
            }

            index++;
        }
    }

    private static void ParseStrings(JsonElement array, string field, List<string> values, List<CapabilityContractError> errors)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                Add(errors, "invalid_collection_item", $"{field}[{index}]", "The collection entry must be a string.");
            }
            else
            {
                values.Add(item.GetString()!);
            }

            index++;
        }
    }

    private static bool ValidateObjectShape(JsonElement element, string field, IReadOnlyCollection<string> expectedProperties, List<CapabilityContractError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            Add(errors, "object_required", field, "A JSON object is required.");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                Add(errors, "duplicate_descriptor_property", $"{field}.{property.Name}", "Descriptor objects cannot contain duplicate properties.");
            }
            else if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                Add(errors, "unknown_descriptor_property", $"{field}.{property.Name}", "Unknown descriptor fields are rejected; metadata cannot self-declare authority, trust, configuration, or secret values.");
            }
        }

        foreach (var property in expectedProperties)
        {
            if (!seen.Contains(property))
            {
                Add(errors, "descriptor_property_required", field == "$" ? property : $"{field}.{property}", "The descriptor property is required.");
            }
        }

        return errors.Count == 0;
    }

    private static bool TryGetBoundedArray(JsonElement parent, string propertyName, string field, int maximum, bool allowEmpty, List<CapabilityContractError> errors, out JsonElement array)
    {
        array = parent.GetProperty(propertyName);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum || !allowEmpty && array.GetArrayLength() == 0)
        {
            Add(errors, "collection_out_of_range", field, $"The field must be an array containing {(allowEmpty ? "zero" : "one")} to {maximum} entries.");
            return false;
        }

        return true;
    }

    private static string? ReadString(JsonElement parent, string propertyName, string field, List<CapabilityContractError> errors)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        Add(errors, "string_required", field, "A JSON string is required.");
        return null;
    }

    private static string? ReadNullableString(JsonElement parent, string propertyName, string field, List<CapabilityContractError> errors)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadString(parent, propertyName, field, errors);
    }

    private static int ReadInt32(JsonElement parent, string propertyName, string field, List<CapabilityContractError> errors)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        Add(errors, "integer_required", field, "A bounded JSON integer is required.");
        return 0;
    }

    private static long ReadInt64(JsonElement parent, string propertyName, string field, List<CapabilityContractError> errors)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
        {
            return value;
        }

        Add(errors, "integer_required", field, "A bounded JSON integer is required.");
        return 0;
    }

    private static CapabilityContractValidationResult Invalid(string code, string field, string message)
    {
        return new CapabilityContractValidationResult([new CapabilityContractError(code, field, message)]);
    }

    private static void Add(List<CapabilityContractError> errors, string code, string field, string message)
    {
        if (errors.Count < 64)
        {
            errors.Add(new CapabilityContractError(code, field, message));
        }
    }
}
