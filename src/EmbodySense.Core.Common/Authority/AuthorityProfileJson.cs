using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Serializes valid schema-version-1 authority profiles into deterministic, compact, bounded JSON.
/// </summary>
public static class AuthorityProfileJson
{
    private static readonly string[] _rootProperties = ["boundaryConditions", "ceiling", "expiresAtUtc", "issuedAtUtc", "profileId", "provenance", "purpose", "revision", "schemaVersion", "status"];
    private static readonly string[] _ceilingProperties = ["allowsExternalPublication", "allowsIrreversibleAction", "allowsRecurrence", "capabilities", "dataClasses", "maxSideEffectClass", "maxTargetCount"];
    private static readonly string[] _provenanceProperties = ["actorId", "kind"];
    private static readonly string[] _conditionProperties = ["decision", "reason"];
    private static readonly string[] _capabilityProperties = ["hash", "id", "version"];

    /// <summary>
    /// Serializes a valid profile with ordinally sorted set-like collections.
    /// </summary>
    /// <param name="profile">The profile to validate and serialize.</param>
    /// <param name="json">The canonical JSON when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when serialization succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TrySerialize(AuthorityProfile? profile, out string? json, out AuthorityContractValidationResult validation)
    {
        validation = AuthorityProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            json = null;
            return false;
        }

        var value = profile!;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteConditions(writer, value.BoundaryConditions);
            WriteCeiling(writer, value.Ceiling);
            if (value.ExpiresAtUtc is { } expiresAtUtc)
            {
                writer.WriteString("expiresAtUtc", ToCanonicalUtc(expiresAtUtc));
            }
            else
            {
                writer.WriteNull("expiresAtUtc");
            }

            writer.WriteString("issuedAtUtc", ToCanonicalUtc(value.IssuedAtUtc));
            writer.WriteString("profileId", value.ProfileId.Value);
            WriteProvenance(writer, value.Provenance);
            writer.WriteString("purpose", value.Purpose.Value);
            writer.WriteNumber("revision", value.Revision.Value);
            writer.WriteNumber("schemaVersion", value.SchemaVersion);
            writer.WriteString("status", AuthorityContractVocabulary.ToCanonical(value.Status));
            writer.WriteEndObject();
            writer.Flush();
        }

        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return true;
    }

    /// <summary>
    /// Parses only the closed, exact canonical schema-version-1 profile JSON form.
    /// </summary>
    /// <param name="json">The candidate profile JSON.</param>
    /// <param name="profile">The parsed profile when successful.</param>
    /// <param name="validation">The structured parse and validation result.</param>
    /// <returns><see langword="true"/> when the JSON is safe, valid, and byte-for-byte canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryDeserialize(string? json, out AuthorityProfile? profile, out AuthorityContractValidationResult validation)
    {
        profile = null;
        if (!AuthorityTextRules.IsSafeNormalized(json, AuthorityContractLimits.MaxProfileJsonCharacters))
        {
            validation = Failure(AuthorityContractErrorCode.InvalidJson, AuthorityContractField.Contract);
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json!, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            if (!TryObject(document.RootElement, _rootProperties, AuthorityContractField.Contract, out var rootError))
            {
                validation = rootError!;
                return false;
            }

            DateTimeOffset issuedAtUtc;
            DateTimeOffset? expiresAtUtc;
            if (!TryInteger(document.RootElement.GetProperty("schemaVersion"), AuthorityContractField.SchemaVersion, out var schemaVersion, out validation)
                || !TryProfileId(document.RootElement, out var profileId, out validation)
                || !TryRevision(document.RootElement, out var revision, out validation)
                || !TryStatus(document.RootElement, out var status, out validation)
                || !TryPurpose(document.RootElement, out var purpose, out validation)
                || !TryProvenance(document.RootElement, out var provenance, out validation)
                || !TryUtc(document.RootElement, "issuedAtUtc", AuthorityContractField.IssuedAtUtc, false, out issuedAtUtc, out validation)
                || !TryUtc(document.RootElement, "expiresAtUtc", AuthorityContractField.ExpiresAtUtc, true, out expiresAtUtc, out validation)
                || !TryCeiling(document.RootElement, out var ceiling, out validation)
                || !TryConditions(document.RootElement, out var conditions, out validation))
            {
                return false;
            }

            profile = new AuthorityProfile(schemaVersion, profileId!, revision!, status, purpose!, provenance!, issuedAtUtc, expiresAtUtc, ceiling!, conditions!);
            validation = AuthorityProfileValidator.Validate(profile);
            if (!validation.IsValid)
            {
                profile = null;
                return false;
            }

            if (!TrySerialize(profile, out var canonical, out validation) || !string.Equals(json, canonical, StringComparison.Ordinal))
            {
                profile = null;
                validation = Failure(AuthorityContractErrorCode.NonCanonicalJson, AuthorityContractField.Contract);
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            validation = Failure(AuthorityContractErrorCode.InvalidJson, AuthorityContractField.Contract);
            return false;
        }
    }

    private static bool TryProfileId(JsonElement root, out AuthorityProfileId? profileId, out AuthorityContractValidationResult validation)
    {
        if (!TryString(root.GetProperty("profileId"), AuthorityContractField.ProfileId, out var value, out validation))
        {
            profileId = null;
            return false;
        }

        if (!AuthorityProfileId.TryParse(value, out profileId, out var error))
        {
            validation = Result(error!);
            return false;
        }

        return true;
    }

    private static bool TryRevision(JsonElement root, out AuthorityProfileRevision? revision, out AuthorityContractValidationResult validation)
    {
        if (!TryRawInteger(root.GetProperty("revision"), AuthorityContractField.Revision, out var value, out validation))
        {
            revision = null;
            return false;
        }

        if (!AuthorityProfileRevision.TryParse(value, out revision, out var error))
        {
            validation = Result(error!);
            return false;
        }

        return true;
    }

    private static bool TryStatus(JsonElement root, out AuthorityProfileStatus status, out AuthorityContractValidationResult validation)
    {
        if (!TryString(root.GetProperty("status"), AuthorityContractField.Status, out var value, out validation))
        {
            status = AuthorityProfileStatus.Unknown;
            return false;
        }

        if (!AuthorityContractVocabulary.TryParseStatus(value, out status))
        {
            validation = Failure(AuthorityContractErrorCode.UnsupportedStatus, AuthorityContractField.Status);
            return false;
        }

        return true;
    }

    private static bool TryPurpose(JsonElement root, out AuthorityPurpose? purpose, out AuthorityContractValidationResult validation)
    {
        if (!TryString(root.GetProperty("purpose"), AuthorityContractField.Purpose, out var value, out validation))
        {
            purpose = null;
            return false;
        }

        if (!AuthorityPurpose.TryParse(value, out purpose, out var error))
        {
            validation = Result(error!);
            return false;
        }

        return true;
    }

    private static bool TryProvenance(JsonElement root, out AuthorityProvenance? provenance, out AuthorityContractValidationResult validation)
    {
        provenance = null;
        var element = root.GetProperty("provenance");
        if (!TryObject(element, _provenanceProperties, AuthorityContractField.Provenance, out var objectError))
        {
            validation = objectError!;
            return false;
        }

        if (!TryString(element.GetProperty("actorId"), AuthorityContractField.ProvenanceActorId, out var actorText, out validation)
            || !TryString(element.GetProperty("kind"), AuthorityContractField.ProvenanceKind, out var kindText, out validation))
        {
            return false;
        }

        if (!AuthorityActorId.TryParse(actorText, out var actorId, out var actorError))
        {
            validation = Result(actorError!);
            return false;
        }

        if (!AuthorityContractVocabulary.TryParseProvenanceKind(kindText, out var kind))
        {
            validation = Failure(AuthorityContractErrorCode.UnsupportedProvenanceKind, AuthorityContractField.ProvenanceKind);
            return false;
        }

        provenance = new AuthorityProvenance(actorId!, kind);
        return true;
    }

    private static bool TryCeiling(JsonElement root, out AuthorityCeiling? ceiling, out AuthorityContractValidationResult validation)
    {
        ceiling = null;
        var element = root.GetProperty("ceiling");
        if (!TryObject(element, _ceilingProperties, AuthorityContractField.Ceiling, out var objectError))
        {
            validation = objectError!;
            return false;
        }

        if (!TryCapabilities(element, out var capabilities, out validation)
            || !TryDataClasses(element, out var dataClasses, out validation)
            || !TryInteger(element.GetProperty("maxTargetCount"), AuthorityContractField.MaxTargetCount, out var maxTargetCount, out validation)
            || !TryString(element.GetProperty("maxSideEffectClass"), AuthorityContractField.MaxSideEffectClass, out var sideEffectText, out validation)
            || !AuthorityContractVocabulary.TryParseSideEffectClass(sideEffectText, out var maxSideEffectClass)
            || !TryBoolean(element.GetProperty("allowsRecurrence"), out var allowsRecurrence, out validation)
            || !TryBoolean(element.GetProperty("allowsExternalPublication"), out var allowsExternalPublication, out validation)
            || !TryBoolean(element.GetProperty("allowsIrreversibleAction"), out var allowsIrreversibleAction, out validation))
        {
            if (validation.IsValid)
            {
                validation = Failure(AuthorityContractErrorCode.UnsupportedSideEffectClass, AuthorityContractField.MaxSideEffectClass);
            }

            return false;
        }

        ceiling = new AuthorityCeiling(capabilities!, dataClasses!, maxTargetCount, maxSideEffectClass, allowsRecurrence, allowsExternalPublication, allowsIrreversibleAction);
        validation = AuthorityProfileValidator.ValidateCeiling(ceiling);
        if (!validation.IsValid)
        {
            ceiling = null;
            return false;
        }

        return true;
    }

    private static bool TryCapabilities(JsonElement ceiling, out IReadOnlyList<CapabilityDescriptorIdentity>? capabilities, out AuthorityContractValidationResult validation)
    {
        capabilities = null;
        validation = new AuthorityContractValidationResult([]);
        var array = ceiling.GetProperty("capabilities");
        if (array.ValueKind != JsonValueKind.Array)
        {
            validation = Failure(AuthorityContractErrorCode.ArrayRequired, AuthorityContractField.Capabilities);
            return false;
        }

        var values = new List<CapabilityDescriptorIdentity>();
        foreach (var element in array.EnumerateArray())
        {
            if (!TryObject(element, _capabilityProperties, AuthorityContractField.Capabilities, out var objectError)
                || !TryString(element.GetProperty("id"), AuthorityContractField.Capabilities, out var idText, out validation)
                || !TryString(element.GetProperty("version"), AuthorityContractField.Capabilities, out var versionText, out validation)
                || !TryString(element.GetProperty("hash"), AuthorityContractField.Capabilities, out var hashText, out validation)
                || !CapabilityId.TryParse(idText, out var id, out _)
                || !CapabilityVersion.TryParse(versionText, out var version, out _)
                || !CapabilityDescriptorHash.TryParse(hashText, out var hash, out _))
            {
                validation = objectError ?? (validation.IsValid ? Failure(AuthorityContractErrorCode.CapabilityIdentityRequired, AuthorityContractField.Capabilities) : validation);
                return false;
            }

            values.Add(new CapabilityDescriptorIdentity(id!, version!, hash!));
        }

        capabilities = values;
        return true;
    }

    private static bool TryDataClasses(JsonElement ceiling, out IReadOnlyList<CapabilityDataClass>? dataClasses, out AuthorityContractValidationResult validation)
    {
        dataClasses = null;
        validation = new AuthorityContractValidationResult([]);
        var array = ceiling.GetProperty("dataClasses");
        if (array.ValueKind != JsonValueKind.Array)
        {
            validation = Failure(AuthorityContractErrorCode.ArrayRequired, AuthorityContractField.DataClasses);
            return false;
        }

        var values = new List<CapabilityDataClass>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || !CapabilityDataClass.TryParse(element.GetString(), out var dataClass, out _))
            {
                validation = Failure(AuthorityContractErrorCode.CollectionItemRequired, AuthorityContractField.DataClasses);
                return false;
            }

            values.Add(dataClass!);
        }

        dataClasses = values;
        return true;
    }

    private static bool TryConditions(JsonElement root, out IReadOnlyList<AuthorityBoundaryCondition>? conditions, out AuthorityContractValidationResult validation)
    {
        conditions = null;
        validation = new AuthorityContractValidationResult([]);
        var array = root.GetProperty("boundaryConditions");
        if (array.ValueKind != JsonValueKind.Array)
        {
            validation = Failure(AuthorityContractErrorCode.ArrayRequired, AuthorityContractField.BoundaryConditions);
            return false;
        }

        var values = new List<AuthorityBoundaryCondition>();
        foreach (var element in array.EnumerateArray())
        {
            if (!TryObject(element, _conditionProperties, AuthorityContractField.BoundaryConditions, out var objectError)
                || !TryString(element.GetProperty("decision"), AuthorityContractField.BoundaryDecision, out var decisionText, out validation)
                || !TryString(element.GetProperty("reason"), AuthorityContractField.BoundaryReason, out var reasonText, out validation)
                || !AuthorityContractVocabulary.TryParseDecision(decisionText, out var decision)
                || !AuthorityContractVocabulary.TryParseReason(reasonText, out var reason))
            {
                validation = objectError ?? (validation.IsValid ? Failure(AuthorityContractErrorCode.InvalidBoundaryCondition, AuthorityContractField.BoundaryConditions) : validation);
                return false;
            }

            values.Add(new AuthorityBoundaryCondition(decision, reason));
        }

        conditions = values;
        return true;
    }

    private static bool TryUtc(JsonElement root, string property, AuthorityContractField field, bool nullable, out DateTimeOffset? value, out AuthorityContractValidationResult validation)
    {
        value = null;
        validation = new AuthorityContractValidationResult([]);
        var element = root.GetProperty(property);

        if (nullable && element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParseExact(element.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero)
        {
            validation = Failure(AuthorityContractErrorCode.InvalidTimestamp, field);
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryUtc(JsonElement root, string property, AuthorityContractField field, bool nullable, out DateTimeOffset value, out AuthorityContractValidationResult validation)
    {
        value = default;
        validation = new AuthorityContractValidationResult([]);
        if (!TryUtc(root, property, field, nullable, out DateTimeOffset? parsed, out validation) || parsed is null)
        {
            if (validation.IsValid)
            {
                validation = Failure(AuthorityContractErrorCode.InvalidTimestamp, field);
            }

            return false;
        }

        value = parsed.Value;
        return true;
    }

    private static bool TryObject(JsonElement element, IReadOnlyCollection<string> allowedProperties, AuthorityContractField field, out AuthorityContractValidationResult? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = Failure(AuthorityContractErrorCode.ObjectRequired, field);
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                error = Failure(AuthorityContractErrorCode.DuplicateProperty, field);
                return false;
            }

            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                error = Failure(AuthorityContractErrorCode.UnknownProperty, field);
                return false;
            }
        }

        foreach (var property in allowedProperties)
        {
            if (!names.Contains(property))
            {
                error = Failure(AuthorityContractErrorCode.PropertyRequired, field);
                return false;
            }
        }

        return true;
    }

    private static bool TryString(JsonElement json, AuthorityContractField field, out string? value, out AuthorityContractValidationResult validation)
    {
        value = null;
        validation = new AuthorityContractValidationResult([]);
        if (json.ValueKind != JsonValueKind.String)
        {
            validation = Failure(AuthorityContractErrorCode.StringRequired, field);
            return false;
        }

        value = json.GetString();
        return true;
    }

    private static bool TryInteger(JsonElement json, AuthorityContractField field, out int value, out AuthorityContractValidationResult validation)
    {
        value = default;
        validation = new AuthorityContractValidationResult([]);
        if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt32(out value))
        {
            validation = Failure(AuthorityContractErrorCode.IntegerRequired, field);
            return false;
        }

        return true;
    }

    private static bool TryRawInteger(JsonElement json, AuthorityContractField field, out string? value, out AuthorityContractValidationResult validation)
    {
        value = null;
        validation = new AuthorityContractValidationResult([]);
        if (json.ValueKind != JsonValueKind.Number)
        {
            validation = Failure(AuthorityContractErrorCode.IntegerRequired, field);
            return false;
        }

        value = json.GetRawText();
        return true;
    }

    private static bool TryBoolean(JsonElement json, out bool value, out AuthorityContractValidationResult validation)
    {
        value = default;
        validation = new AuthorityContractValidationResult([]);
        if (json.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            validation = Failure(AuthorityContractErrorCode.BooleanRequired, AuthorityContractField.Ceiling);
            return false;
        }

        value = json.GetBoolean();
        return true;
    }

    private static AuthorityContractValidationResult Failure(AuthorityContractErrorCode code, AuthorityContractField field)
    {
        return new AuthorityContractValidationResult([new AuthorityContractError(code, field)]);
    }

    private static AuthorityContractValidationResult Result(AuthorityContractError error)
    {
        return new AuthorityContractValidationResult([error]);
    }

    private static void WriteConditions(Utf8JsonWriter writer, IReadOnlyList<AuthorityBoundaryCondition> conditions)
    {
        writer.WritePropertyName("boundaryConditions");
        writer.WriteStartArray();
        foreach (var condition in conditions.OrderBy(condition => condition.Decision).ThenBy(condition => condition.Reason))
        {
            writer.WriteStartObject();
            writer.WriteString("decision", AuthorityContractVocabulary.ToCanonical(condition.Decision));
            writer.WriteString("reason", AuthorityContractVocabulary.ToCanonical(condition.Reason));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteCeiling(Utf8JsonWriter writer, AuthorityCeiling ceiling)
    {
        writer.WritePropertyName("ceiling");
        writer.WriteStartObject();
        writer.WriteBoolean("allowsExternalPublication", ceiling.AllowsExternalPublication);
        writer.WriteBoolean("allowsIrreversibleAction", ceiling.AllowsIrreversibleAction);
        writer.WriteBoolean("allowsRecurrence", ceiling.AllowsRecurrence);
        writer.WritePropertyName("capabilities");
        writer.WriteStartArray();
        foreach (var capability in ceiling.Capabilities.OrderBy(value => value.Id).ThenBy(value => value.Version).ThenBy(value => value.Hash.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("hash", capability.Hash.Value);
            writer.WriteString("id", capability.Id.Value);
            writer.WriteString("version", capability.Version.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("dataClasses");
        writer.WriteStartArray();
        foreach (var dataClass in ceiling.DataClasses.OrderBy(value => value))
        {
            writer.WriteStringValue(dataClass.Value);
        }

        writer.WriteEndArray();
        writer.WriteString("maxSideEffectClass", AuthorityContractVocabulary.ToCanonical(ceiling.MaxSideEffectClass));
        writer.WriteNumber("maxTargetCount", ceiling.MaxTargetCount);
        writer.WriteEndObject();
    }

    private static void WriteProvenance(Utf8JsonWriter writer, AuthorityProvenance provenance)
    {
        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        writer.WriteString("actorId", provenance.ActorId.Value);
        writer.WriteString("kind", AuthorityContractVocabulary.ToCanonical(provenance.Kind));
        writer.WriteEndObject();
    }

    private static string ToCanonicalUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
