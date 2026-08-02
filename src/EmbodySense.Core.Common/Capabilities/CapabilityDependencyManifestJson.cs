using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>Serializes and parses closed canonical schema-version-1 dependency manifests.</summary>
public static class CapabilityDependencyManifestJson
{
    private static readonly string[] _rootProperties = ["artifact", "kind", "optional", "required", "schemaVersion", "subjectId"];
    private static readonly string[] _artifactProperties = ["checksum", "signature"];
    private static readonly string[] _dependencyProperties = ["capabilityId", "compatibleVersionRange"];

    /// <summary>Serializes a valid manifest to deterministic compact JSON.</summary>
    public static bool TrySerialize(CapabilityDependencyManifest? manifest, out string? json, out CapabilityContractValidationResult validation)
    {
        validation = CapabilityDependencyManifestValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            json = null;
            return false;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("artifact");
            writer.WriteStartObject();
            writer.WriteString("checksum", manifest!.Artifact.Checksum?.Value);
            writer.WriteString("signature", manifest.Artifact.Signature);
            writer.WriteEndObject();
            writer.WriteString("kind", ToText(manifest.Kind));
            WriteDependencies(writer, "optional", manifest.Optional);
            WriteDependencies(writer, "required", manifest.Required);
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("subjectId", manifest.SubjectId.Value);
            writer.WriteEndObject();
        }

        json = Encoding.UTF8.GetString(stream.ToArray());
        if (json.Length <= CapabilityContractLimits.MaxDependencyManifestJsonCharacters)
        {
            return true;
        }

        json = null;
        validation = Invalid("dependency_manifest_json_too_large", "$", "The canonical dependency manifest exceeds the schema-1 size bound.");
        return false;
    }

    /// <summary>Parses closed dependency manifest JSON, rejecting authority and configuration metadata.</summary>
    public static bool TryDeserialize(string? json, out CapabilityDependencyManifest? manifest, out CapabilityContractValidationResult validation)
    {
        manifest = null;
        if (string.IsNullOrEmpty(json) || json.Length > CapabilityContractLimits.MaxDependencyManifestJsonCharacters || !CapabilityTextRules.IsSafeNormalized(json, CapabilityContractLimits.MaxDependencyManifestJsonCharacters, allowEmpty: false))
        {
            validation = Invalid("invalid_dependency_manifest_json", "$", "Dependency manifest JSON must be non-empty, bounded, normalized, and free of unsafe Unicode.");
            return false;
        }

        var errors = new List<CapabilityContractError>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            var root = document.RootElement;
            if (!ValidateShape(root, "$", _rootProperties, errors))
            {
                validation = new CapabilityContractValidationResult(errors);
                return false;
            }

            var schemaVersion = ReadInteger(root, "schemaVersion", "schemaVersion", errors);
            var kind = ParseKind(ReadString(root, "kind", "kind", errors), errors);
            var subject = ParseId(ReadString(root, "subjectId", "subjectId", errors), "subjectId", errors);
            var required = ParseDependencies(root.GetProperty("required"), "required", errors);
            var optional = ParseDependencies(root.GetProperty("optional"), "optional", errors);
            var artifact = ParseArtifact(root.GetProperty("artifact"), errors);
            if (errors.Count > 0)
            {
                validation = new CapabilityContractValidationResult(errors);
                return false;
            }

            manifest = new CapabilityDependencyManifest(schemaVersion, kind, subject!, required!, optional!, artifact!);
            validation = CapabilityDependencyManifestValidator.Validate(manifest);
            if (!validation.IsValid)
            {
                manifest = null;
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            validation = Invalid("invalid_dependency_manifest_json", "$", $"The dependency manifest JSON is malformed: {exception.Message}");
            return false;
        }
    }

    private static void WriteDependencies(Utf8JsonWriter writer, string property, IReadOnlyList<CapabilityDependency> dependencies)
    {
        writer.WritePropertyName(property);
        writer.WriteStartArray();
        foreach (var dependency in dependencies.OrderBy(item => item.CapabilityId.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("capabilityId", dependency.CapabilityId.Value);
            writer.WriteString("compatibleVersionRange", dependency.CompatibleVersionRange.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static CapabilityDependencyManifestKind ParseKind(string? value, List<CapabilityContractError> errors)
    {
        var kind = value switch { "skill" => CapabilityDependencyManifestKind.Skill, "loop-package" => CapabilityDependencyManifestKind.LoopPackage, "capability-package" => CapabilityDependencyManifestKind.CapabilityPackage, _ => CapabilityDependencyManifestKind.Unknown };
        if (kind == CapabilityDependencyManifestKind.Unknown)
        {
            Add(errors, "unsupported_dependency_manifest_kind", "kind", "The dependency manifest kind is absent or unsupported.");
        }

        return kind;
    }

    private static CapabilityId? ParseId(string? value, string field, List<CapabilityContractError> errors)
    {
        if (CapabilityId.TryParse(value, out var id, out _))
        {
            return id;
        }

        Add(errors, "invalid_capability_id", field, "Capability ids must be canonical.");
        return null;
    }

    private static List<CapabilityDependency>? ParseDependencies(JsonElement element, string field, List<CapabilityContractError> errors)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > CapabilityContractLimits.MaxDependencyManifestDependencies)
        {
            Add(errors, "dependency_collection_out_of_range", field, "The dependency collection is outside the schema-1 bound.");
            return null;
        }

        var dependencies = new List<CapabilityDependency>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!ValidateShape(item, $"{field}[{index}]", _dependencyProperties, errors))
            {
                index++;
                continue;
            }

            var id = ParseId(ReadString(item, "capabilityId", $"{field}[{index}].capabilityId", errors), $"{field}[{index}].capabilityId", errors);
            var rangeText = ReadString(item, "compatibleVersionRange", $"{field}[{index}].compatibleVersionRange", errors);
            if (!CapabilityVersionRange.TryParse(rangeText, out var range, out _))
            {
                Add(errors, "invalid_capability_version_range", $"{field}[{index}].compatibleVersionRange", "Dependency version ranges must be canonical.");
            }
            else if (id is not null)
            {
                dependencies.Add(new CapabilityDependency(id, range!));
            }

            index++;
        }

        return dependencies;
    }

    private static CapabilityDependencyArtifactMetadata? ParseArtifact(JsonElement element, List<CapabilityContractError> errors)
    {
        if (!ValidateShape(element, "artifact", _artifactProperties, errors))
        {
            return null;
        }

        var checksumText = ReadNullableString(element, "checksum", "artifact.checksum", errors);
        CapabilityIntegrityDigest? checksum = null;
        if (checksumText is not null && !CapabilityIntegrityDigest.TryParse(checksumText, out checksum, out _))
        {
            Add(errors, "invalid_integrity_digest", "artifact.checksum", "Artifact checksums must use canonical SHA-256 form.");
        }

        return new CapabilityDependencyArtifactMetadata(checksum, ReadNullableString(element, "signature", "artifact.signature", errors));
    }

    private static bool ValidateShape(JsonElement element, string field, IReadOnlyCollection<string> expected, List<CapabilityContractError> errors)
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
                Add(errors, "duplicate_dependency_manifest_property", $"{field}.{property.Name}", "Dependency manifest objects cannot contain duplicate properties.");
            }
            else if (!expected.Contains(property.Name, StringComparer.Ordinal))
            {
                Add(errors, "unknown_dependency_manifest_property", $"{field}.{property.Name}", "Unknown dependency manifest fields are rejected; manifests cannot carry authority, permissions, secrets, or executable configuration.");
            }
        }

        foreach (var property in expected)
        {
            if (!seen.Contains(property))
            {
                Add(errors, "dependency_manifest_property_required", field == "$" ? property : $"{field}.{property}", "The dependency manifest property is required.");
            }
        }

        return errors.Count == 0;
    }

    private static string? ReadString(JsonElement parent, string property, string field, List<CapabilityContractError> errors)
    {
        var element = parent.GetProperty(property);
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        Add(errors, "string_required", field, "A JSON string is required.");
        return null;
    }

    private static string? ReadNullableString(JsonElement parent, string property, string field, List<CapabilityContractError> errors) => parent.GetProperty(property).ValueKind == JsonValueKind.Null ? null : ReadString(parent, property, field, errors);

    private static int ReadInteger(JsonElement parent, string property, string field, List<CapabilityContractError> errors)
    {
        if (parent.GetProperty(property).TryGetInt32(out var value))
        {
            return value;
        }

        Add(errors, "integer_required", field, "A bounded JSON integer is required.");
        return 0;
    }

    private static string ToText(CapabilityDependencyManifestKind kind) => kind switch { CapabilityDependencyManifestKind.Skill => "skill", CapabilityDependencyManifestKind.LoopPackage => "loop-package", CapabilityDependencyManifestKind.CapabilityPackage => "capability-package", _ => string.Empty };

    private static CapabilityContractValidationResult Invalid(string code, string field, string message) => new([new CapabilityContractError(code, field, message)]);

    private static void Add(List<CapabilityContractError> errors, string code, string field, string message)
    {
        if (errors.Count < 64)
        {
            errors.Add(new CapabilityContractError(code, field, message));
        }
    }
}
