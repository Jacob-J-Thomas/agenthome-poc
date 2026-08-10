using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Authority.Grants;

/// <summary>Serializes and parses the closed canonical schema-version-1 authority-grant representation.</summary>
public static class AuthorityGrantJson
{
    private static readonly string[] _rootProperties = ["binding", "boundary", "changedByActorId", "contentHash", "grantId", "predecessorContentHash", "predecessorRevision", "reason", "recordedAtUtc", "requestedCeiling", "revision", "schemaVersion", "status"];
    private static readonly string[] _bindingProperties = ["loop", "profile", "role"];
    private static readonly string[] _profileProperties = ["contentHash", "profileId", "revision"];
    private static readonly string[] _roleProperties = ["contentHash", "revision", "roleId"];
    private static readonly string[] _loopProperties = ["executableHash", "graphId", "publicationOperationId", "revisionId", "schemaVersion", "validationEvidenceHash"];
    private static readonly string[] _boundaryProperties = ["completionConstraint", "effectiveAtUtc", "expiresAtUtc"];
    private static readonly string[] _ceilingProperties = ["allowsExternalPublication", "allowsIrreversibleAction", "allowsRecurrence", "capabilities", "dataClasses", "maxSideEffectClass", "maxTargetCount"];
    private static readonly string[] _capabilityProperties = ["hash", "id", "version"];

    /// <summary>Serializes one valid immutable grant revision into compact deterministic JSON.</summary>
    /// <param name="grant">The complete grant revision to validate and serialize.</param>
    /// <param name="json">The canonical JSON when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when serialization succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TrySerialize(AuthorityGrant? grant, out string? json, out AuthorityGrantValidationResult validation)
    {
        validation = AuthorityGrantContractValidator.Validate(grant);
        if (!validation.IsValid)
        {
            json = null;
            return false;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteGrant(writer, grant!);
        }

        if (buffer.WrittenCount > AuthorityGrantContractLimits.MaxGrantJsonCharacters)
        {
            json = null;
            validation = Failure(AuthorityGrantValidationErrorCode.InvalidJson, "$", "Canonical authority-grant JSON exceeds the schema-version-1 bound.");
            return false;
        }

        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return true;
    }

    /// <summary>Parses only byte-for-byte canonical, duplicate-free schema-version-1 authority-grant JSON.</summary>
    /// <param name="json">The candidate canonical JSON.</param>
    /// <param name="grant">The parsed immutable grant revision when successful.</param>
    /// <param name="validation">The structured parse or contract validation result.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryDeserialize(string? json, out AuthorityGrant? grant, out AuthorityGrantValidationResult validation)
    {
        grant = null;
        if (!AuthorityTextRules.IsSafeNormalized(json, AuthorityGrantContractLimits.MaxGrantJsonCharacters)
            || Encoding.UTF8.GetByteCount(json!) > AuthorityGrantContractLimits.MaxGrantJsonCharacters)
        {
            validation = Failure(AuthorityGrantValidationErrorCode.InvalidJson, "$", "Authority-grant JSON must be safe, normalized, non-empty, and within the schema-version-1 bound.");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json!, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 12 });
            if (!TryBuildGrant(document.RootElement, out grant))
            {
                validation = Failure(AuthorityGrantValidationErrorCode.InvalidJson, "$", "Authority-grant JSON must match the exact closed schema-version-1 shape.");
                return false;
            }

            validation = AuthorityGrantContractValidator.Validate(grant);
            if (!validation.IsValid)
            {
                grant = null;
                return false;
            }

            if (!TrySerialize(grant, out var canonical, out validation) || !string.Equals(json, canonical, StringComparison.Ordinal))
            {
                grant = null;
                validation = Failure(AuthorityGrantValidationErrorCode.NonCanonicalJson, "$", "Authority-grant JSON must use the single canonical schema-version-1 representation.");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            grant = null;
            validation = Failure(AuthorityGrantValidationErrorCode.InvalidJson, "$", "Authority-grant JSON is malformed or contains an invalid bounded value.");
            return false;
        }
    }

    private static bool TryBuildGrant(JsonElement root, out AuthorityGrant? grant)
    {
        grant = null;
        if (!IsExactObject(root, _rootProperties)
            || !TryInt32(root, "schemaVersion", out var schemaVersion)
            || !TryString(root, "grantId", out var grantIdText)
            || !AuthorityGrantId.TryParse(grantIdText, out var grantId, out _)
            || !TryPositiveRevision(root, "revision", out var revision)
            || !TryNullablePositiveRevision(root, "predecessorRevision", out var predecessorRevision)
            || !TryNullableString(root, "predecessorContentHash", out var predecessorContentHash)
            || !TryString(root, "status", out var statusText)
            || !TryParseStatus(statusText, out var status)
            || !TryBinding(root.GetProperty("binding"), out var binding)
            || !TryCeiling(root.GetProperty("requestedCeiling"), out var requestedCeiling)
            || !TryBoundary(root.GetProperty("boundary"), out var boundary)
            || !TryString(root, "changedByActorId", out var actorText)
            || !AuthorityActorId.TryParse(actorText, out var actorId, out _)
            || !TryString(root, "reason", out var reasonText)
            || !AuthorityPurpose.TryParse(reasonText, out var reason, out _)
            || !TryUtc(root, "recordedAtUtc", false, out var recordedAtUtc)
            || !TryString(root, "contentHash", out var contentHash))
        {
            return false;
        }

        grant = new AuthorityGrant(
            schemaVersion,
            grantId!,
            revision!,
            predecessorRevision,
            predecessorContentHash,
            status,
            binding!,
            requestedCeiling!,
            boundary!,
            actorId!,
            reason!,
            recordedAtUtc!.Value,
            contentHash!);
        return true;
    }

    private static bool TryBinding(JsonElement element, out AuthorityGrantBinding? binding)
    {
        binding = null;
        if (!IsExactObject(element, _bindingProperties)
            || !TryProfilePin(element.GetProperty("profile"), out var profile)
            || !TryRolePin(element.GetProperty("role"), out var role)
            || !TryLoopPin(element.GetProperty("loop"), out var loop))
        {
            return false;
        }

        binding = new AuthorityGrantBinding(profile!, role!, loop!);
        return true;
    }

    private static bool TryProfilePin(JsonElement element, out AuthorityGrantProfilePin? pin)
    {
        pin = null;
        if (!IsExactObject(element, _profileProperties)
            || !TryString(element, "profileId", out var profileIdText)
            || !AuthorityProfileId.TryParse(profileIdText, out var profileId, out _)
            || !TryRawInt32(element, "revision", out var revisionText)
            || !AuthorityProfileRevision.TryParse(revisionText, out var revision, out _)
            || !TryString(element, "contentHash", out var hashText)
            || !AuthorityProfileHash.TryParse(hashText, out var hash, out _))
        {
            return false;
        }

        pin = new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, revision!), hash!);
        return true;
    }

    private static bool TryRolePin(JsonElement element, out AuthorityGrantRolePin? pin)
    {
        pin = null;
        if (!IsExactObject(element, _roleProperties)
            || !TryString(element, "roleId", out var roleId)
            || !ContextualRoleId.IsValid(roleId)
            || !TryInt32(element, "revision", out var revision)
            || revision < 1
            || !TryString(element, "contentHash", out var contentHash)
            || !IsLowerSha256(contentHash))
        {
            return false;
        }

        pin = new AuthorityGrantRolePin(new ContextualRoleRevisionIdentity(roleId!, revision), contentHash!);
        return true;
    }

    private static bool TryLoopPin(JsonElement element, out GovernedLoopRevisionPublicationPin? pin)
    {
        pin = null;
        if (!IsExactObject(element, _loopProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "graphId", out var graphId)
            || !TryString(element, "revisionId", out var revisionId)
            || !TryString(element, "executableHash", out var executableHash)
            || !TryString(element, "publicationOperationId", out var operationId)
            || !TryString(element, "validationEvidenceHash", out var validationHash))
        {
            return false;
        }

        var revision = GovernedLoopRevisionReference.Create(schemaVersion, graphId!, revisionId!, executableHash!);
        pin = new GovernedLoopRevisionPublicationPin(schemaVersion, revision, operationId!, validationHash!);
        return true;
    }

    private static bool TryCeiling(JsonElement element, out AuthorityCeiling? ceiling)
    {
        ceiling = null;
        if (!IsExactObject(element, _ceilingProperties)
            || !TryCapabilities(element.GetProperty("capabilities"), out var capabilities)
            || !TryDataClasses(element.GetProperty("dataClasses"), out var dataClasses)
            || !TryInt32(element, "maxTargetCount", out var maxTargetCount)
            || !TryString(element, "maxSideEffectClass", out var sideEffectText)
            || !AuthorityContractVocabulary.TryParseSideEffectClass(sideEffectText, out var maxSideEffectClass)
            || !TryBoolean(element, "allowsRecurrence", out var allowsRecurrence)
            || !TryBoolean(element, "allowsExternalPublication", out var allowsExternalPublication)
            || !TryBoolean(element, "allowsIrreversibleAction", out var allowsIrreversibleAction))
        {
            return false;
        }

        ceiling = new AuthorityCeiling(capabilities!, dataClasses!, maxTargetCount, maxSideEffectClass, allowsRecurrence, allowsExternalPublication, allowsIrreversibleAction);
        return AuthorityProfileValidator.ValidateCeiling(ceiling).IsValid;
    }

    private static bool TryCapabilities(JsonElement element, out IReadOnlyList<CapabilityDescriptorIdentity>? capabilities)
    {
        capabilities = null;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > AuthorityContractLimits.MaxCapabilitiesPerCeiling)
        {
            return false;
        }

        var values = new List<CapabilityDescriptorIdentity>();
        foreach (var item in element.EnumerateArray())
        {
            if (!IsExactObject(item, _capabilityProperties)
                || !TryString(item, "id", out var idText)
                || !CapabilityId.TryParse(idText, out var id, out _)
                || !TryString(item, "version", out var versionText)
                || !CapabilityVersion.TryParse(versionText, out var version, out _)
                || !TryString(item, "hash", out var hashText)
                || !CapabilityDescriptorHash.TryParse(hashText, out var hash, out _))
            {
                return false;
            }

            values.Add(new CapabilityDescriptorIdentity(id!, version!, hash!));
        }

        capabilities = values;
        return true;
    }

    private static bool TryDataClasses(JsonElement element, out IReadOnlyList<CapabilityDataClass>? dataClasses)
    {
        dataClasses = null;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > AuthorityContractLimits.MaxDataClassesPerCeiling)
        {
            return false;
        }

        var values = new List<CapabilityDataClass>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !CapabilityDataClass.TryParse(item.GetString(), out var dataClass, out _))
            {
                return false;
            }

            values.Add(dataClass!);
        }

        dataClasses = values;
        return true;
    }

    private static bool TryBoundary(JsonElement element, out AuthorityGrantBoundary? boundary)
    {
        boundary = null;
        if (!IsExactObject(element, _boundaryProperties)
            || !TryUtc(element, "effectiveAtUtc", false, out var effectiveAtUtc)
            || !TryUtc(element, "expiresAtUtc", true, out var expiresAtUtc)
            || !TryString(element, "completionConstraint", out var constraintText)
            || !TryParseCompletionConstraint(constraintText, out var completionConstraint))
        {
            return false;
        }

        boundary = new AuthorityGrantBoundary(effectiveAtUtc!.Value, expiresAtUtc, completionConstraint);
        return true;
    }

    private static bool IsExactObject(JsonElement element, IReadOnlyCollection<string> expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name) || !expectedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return names.Count == expectedProperties.Count && expectedProperties.All(names.Contains);
    }

    private static bool TryString(JsonElement parent, string propertyName, out string? value)
    {
        var element = parent.GetProperty(propertyName);
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryNullableString(JsonElement parent, string propertyName, out string? value)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        return TryString(parent, propertyName, out value);
    }

    private static bool TryInt32(JsonElement parent, string propertyName, out int value)
    {
        var element = parent.GetProperty(propertyName);
        value = default;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryRawInt32(JsonElement parent, string propertyName, out string? value)
    {
        var element = parent.GetProperty(propertyName);
        value = element.ValueKind == JsonValueKind.Number ? element.GetRawText() : null;
        return value is not null;
    }

    private static bool TryPositiveRevision(JsonElement parent, string propertyName, out AuthorityGrantRevision? revision)
    {
        revision = null;
        return TryRawInt32(parent, propertyName, out var raw) && AuthorityGrantRevision.TryParse(raw, out revision, out _);
    }

    private static bool TryNullablePositiveRevision(JsonElement parent, string propertyName, out AuthorityGrantRevision? revision)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Null)
        {
            revision = null;
            return true;
        }

        return TryPositiveRevision(parent, propertyName, out revision);
    }

    private static bool TryBoolean(JsonElement parent, string propertyName, out bool value)
    {
        var element = parent.GetProperty(propertyName);
        value = default;
        if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryUtc(JsonElement parent, string propertyName, bool nullable, out DateTimeOffset? value)
    {
        value = null;
        var element = parent.GetProperty(propertyName);
        if (nullable && element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(element.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseStatus(string? value, out AuthorityGrantLifecycleStatus status)
    {
        status = value switch
        {
            "active" => AuthorityGrantLifecycleStatus.Active,
            "suspended" => AuthorityGrantLifecycleStatus.Suspended,
            "revoked" => AuthorityGrantLifecycleStatus.Revoked,
            "expired" => AuthorityGrantLifecycleStatus.Expired,
            _ => AuthorityGrantLifecycleStatus.Unknown,
        };
        return status != AuthorityGrantLifecycleStatus.Unknown;
    }

    private static bool TryParseCompletionConstraint(string? value, out AuthorityGrantCompletionConstraintKind constraint)
    {
        constraint = value switch
        {
            "none" => AuthorityGrantCompletionConstraintKind.None,
            "first-bound-run-completion" => AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion,
            _ => AuthorityGrantCompletionConstraintKind.Unknown,
        };
        return constraint != AuthorityGrantCompletionConstraintKind.Unknown;
    }

    private static string ToCanonical(AuthorityGrantLifecycleStatus value) => value switch
    {
        AuthorityGrantLifecycleStatus.Active => "active",
        AuthorityGrantLifecycleStatus.Suspended => "suspended",
        AuthorityGrantLifecycleStatus.Revoked => "revoked",
        AuthorityGrantLifecycleStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToCanonical(AuthorityGrantCompletionConstraintKind value) => value switch
    {
        AuthorityGrantCompletionConstraintKind.None => "none",
        AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion => "first-bound-run-completion",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteGrant(Utf8JsonWriter writer, AuthorityGrant grant)
    {
        writer.WriteStartObject();
        WriteBinding(writer, grant.Binding);
        WriteBoundary(writer, grant.Boundary);
        writer.WriteString("changedByActorId", grant.ChangedByActorId.Value);
        writer.WriteString("contentHash", grant.ContentHash);
        writer.WriteString("grantId", grant.GrantId.Value);
        writer.WriteString("predecessorContentHash", grant.PredecessorContentHash);
        if (grant.PredecessorRevision is null)
        {
            writer.WriteNull("predecessorRevision");
        }
        else
        {
            writer.WriteNumber("predecessorRevision", grant.PredecessorRevision.Value);
        }

        writer.WriteString("reason", grant.Reason.Value);
        writer.WriteString("recordedAtUtc", ToCanonicalUtc(grant.RecordedAtUtc));
        WriteCeiling(writer, grant.RequestedCeiling);
        writer.WriteNumber("revision", grant.Revision.Value);
        writer.WriteNumber("schemaVersion", grant.SchemaVersion);
        writer.WriteString("status", ToCanonical(grant.Status));
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteBinding(Utf8JsonWriter writer, AuthorityGrantBinding binding)
    {
        writer.WriteStartObject("binding");
        writer.WriteStartObject("loop");
        writer.WriteString("executableHash", binding.Loop.Revision.ExecutableHash);
        writer.WriteString("graphId", binding.Loop.Revision.GraphId);
        writer.WriteString("publicationOperationId", binding.Loop.PublicationOperationId);
        writer.WriteString("revisionId", binding.Loop.Revision.RevisionId);
        writer.WriteNumber("schemaVersion", binding.Loop.SchemaVersion);
        writer.WriteString("validationEvidenceHash", binding.Loop.ValidationEvidenceHash);
        writer.WriteEndObject();
        writer.WriteStartObject("profile");
        writer.WriteString("contentHash", binding.Profile.ContentHash.Value);
        writer.WriteString("profileId", binding.Profile.Reference.ProfileId.Value);
        writer.WriteNumber("revision", binding.Profile.Reference.Revision.Value);
        writer.WriteEndObject();
        writer.WriteStartObject("role");
        writer.WriteString("contentHash", binding.Role.ContentHash);
        writer.WriteNumber("revision", binding.Role.Identity.Revision);
        writer.WriteString("roleId", binding.Role.Identity.RoleId);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteBoundary(Utf8JsonWriter writer, AuthorityGrantBoundary boundary)
    {
        writer.WriteStartObject("boundary");
        writer.WriteString("completionConstraint", ToCanonical(boundary.CompletionConstraint));
        writer.WriteString("effectiveAtUtc", ToCanonicalUtc(boundary.EffectiveAtUtc));
        if (boundary.ExpiresAtUtc is { } expiry)
        {
            writer.WriteString("expiresAtUtc", ToCanonicalUtc(expiry));
        }
        else
        {
            writer.WriteNull("expiresAtUtc");
        }

        writer.WriteEndObject();
    }

    private static void WriteCeiling(Utf8JsonWriter writer, AuthorityCeiling ceiling)
    {
        writer.WriteStartObject("requestedCeiling");
        writer.WriteBoolean("allowsExternalPublication", ceiling.AllowsExternalPublication);
        writer.WriteBoolean("allowsIrreversibleAction", ceiling.AllowsIrreversibleAction);
        writer.WriteBoolean("allowsRecurrence", ceiling.AllowsRecurrence);
        writer.WriteStartArray("capabilities");
        foreach (var capability in ceiling.Capabilities.OrderBy(value => value.Id).ThenBy(value => value.Version).ThenBy(value => value.Hash.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("hash", capability.Hash.Value);
            writer.WriteString("id", capability.Id.Value);
            writer.WriteString("version", capability.Version.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("dataClasses");
        foreach (var dataClass in ceiling.DataClasses.OrderBy(value => value))
        {
            writer.WriteStringValue(dataClass.Value);
        }

        writer.WriteEndArray();
        writer.WriteString("maxSideEffectClass", AuthorityContractVocabulary.ToCanonical(ceiling.MaxSideEffectClass));
        writer.WriteNumber("maxTargetCount", ceiling.MaxTargetCount);
        writer.WriteEndObject();
    }

    private static string ToCanonicalUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static AuthorityGrantValidationResult Failure(AuthorityGrantValidationErrorCode code, string path, string message)
        => new(Array.AsReadOnly([new AuthorityGrantValidationError(code, path, message)]), false);
}
