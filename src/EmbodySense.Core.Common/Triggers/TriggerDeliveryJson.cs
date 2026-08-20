using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Serializes and parses only the exact canonical schema-version-1 trigger-delivery JSON form.
/// </summary>
public static class TriggerDeliveryJson
{
    private static readonly string[] _rootProperties = ["actorContext", "adapter", "authority", "deduplicationId", "deliveryId", "invokingConversation", "kind", "loop", "payload", "publicationRequested", "redelivery", "scheduleExecutionDirective", "schemaVersion", "temporal", "visibleReason", "visibleStatus"];
    private static readonly string[] _actorProperties = ["actorId", "roleId", "surfaceId", "workspaceId"];
    private static readonly string[] _adapterProperties = ["capability", "implementation"];
    private static readonly string[] _capabilityProperties = ["hash", "id", "version"];
    private static readonly string[] _implementationProperties = ["implementationId", "providerId"];
    private static readonly string[] _authorityProperties = ["boundaryReceipt", "profile"];
    private static readonly string[] _profileProperties = ["profileId", "revision"];
    private static readonly string[] _receiptProperties = ["conditions", "decision", "evaluatedAtUtc", "profiles", "schemaVersion"];
    private static readonly string[] _conditionProperties = ["decision", "reason"];
    private static readonly string[] _conversationProperties = ["capturedAtUtc", "capturedVersion", "conversationId"];
    private static readonly string[] _loopProperties = ["authorityGrant", "governedPublication", "kind", "legacyDefinition"];
    private static readonly string[] _legacyDefinitionProperties = ["contentHash", "definitionVersion", "loopId"];
    private static readonly string[] _governedPublicationProperties = ["executableHash", "graphId", "publicationOperationId", "revisionId", "schemaVersion", "validationEvidenceHash"];
    private static readonly string[] _authorityGrantProperties = ["contentHash", "grantId", "revision"];
    private static readonly string[] _payloadProperties = ["contentHash", "governedReference", "inlineBase64"];
    private static readonly string[] _redeliveryProperties = ["attempt", "count", "originalDeliveryId"];
    private static readonly string[] _temporalProperties = ["admittedAtUtc", "createdAtUtc", "deadlineUtc", "expiresAtUtc", "notBeforeUtc", "observedAtUtc", "receivedAtUtc"];
    private static readonly string[] _scheduleDirectiveProperties = ["definitionHash", "definitionRevision", "identity", "occurrence", "overlap", "preQueueOverlapEvidenceHash", "scheduleId", "schemaVersion", "target"];
    private static readonly string[] _scheduleOccurrenceProperties = ["ordinal", "scheduledAtUtc", "scheduledLocal", "schemaVersion", "timeZone"];
    private static readonly string[] _scheduleIdentityProperties = ["deduplicationId", "deliveryId", "occurrenceId"];
    private static readonly string[] _scheduleTimeZoneProperties = ["rulesFingerprint", "timeZoneId"];

    /// <summary>
    /// Serializes a valid envelope into bounded deterministic UTF-8 JSON.
    /// </summary>
    /// <param name="envelope">The envelope to validate and serialize.</param>
    /// <param name="json">The canonical JSON when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when serialization succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TrySerialize(TriggerDeliveryEnvelope? envelope, out string? json, out TriggerContractValidationResult validation)
    {
        validation = TriggerDeliveryValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            json = null;
            return false;
        }

        if (!TrySerializeKnownValid(envelope!, out json, out var error))
        {
            validation = new TriggerContractValidationResult([error!]);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses only byte-for-byte canonical, duplicate-free schema-version-1 JSON.
    /// </summary>
    /// <param name="json">The candidate JSON.</param>
    /// <param name="envelope">The parsed envelope when successful.</param>
    /// <param name="validation">The structured parse or validation result.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryDeserialize(string? json, out TriggerDeliveryEnvelope? envelope, out TriggerContractValidationResult validation)
    {
        envelope = null;
        if (!TriggerTextRules.IsSafeNormalized(json, TriggerDeliveryLimits.MaxCanonicalDocumentUtf8Bytes) || Encoding.UTF8.GetByteCount(json!) > TriggerDeliveryLimits.MaxCanonicalDocumentUtf8Bytes)
        {
            validation = Failure("invalid_json", "$");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json!, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 12 });
            var root = document.RootElement;
            if (!IsExactObject(root, _rootProperties)
                || !TryInteger(root, "schemaVersion", out var schemaVersion)
                || !TryString(root, "deliveryId", out var deliveryText)
                || !TriggerDeliveryId.TryParse(deliveryText, out var deliveryId)
                || !TryString(root, "deduplicationId", out var deduplicationText)
                || !TriggerDeduplicationId.TryParse(deduplicationText, out var deduplicationId)
                || !TryString(root, "kind", out var kindText)
                || !TriggerVocabulary.TryParseKind(kindText, out var kind)
                || !TryAdapter(root.GetProperty("adapter"), out var adapter)
                || !TryLoop(root.GetProperty("loop"), out var loop)
                || !TryActor(root.GetProperty("actorContext"), out var actorContext)
                || !TryAuthority(root.GetProperty("authority"), out var authority)
                || !TryTemporal(root.GetProperty("temporal"), out var temporal)
                || !TryPayload(root.GetProperty("payload"), out var payload)
                || !TryRedelivery(root.GetProperty("redelivery"), out var redelivery)
                || !TryScheduleExecutionDirective(root.GetProperty("scheduleExecutionDirective"), out var scheduleExecutionDirective)
                || !TryBoolean(root, "publicationRequested", out var publicationRequested)
                || !TryConversation(root.GetProperty("invokingConversation"), out var conversation)
                || !TryString(root, "visibleStatus", out var statusText)
                || !TriggerVocabulary.TryParseStatus(statusText, out var visibleStatus)
                || !TryString(root, "visibleReason", out var reasonText)
                || !TriggerVocabulary.TryParseReason(reasonText, out var visibleReason))
            {
                validation = Failure("invalid_json_shape", "$");
                return false;
            }

            bool created;
            if (kind == TriggerKind.Time)
            {
                created = TriggerDeliveryFactory.TryCreateScheduledEnvelope(
                    schemaVersion,
                    deliveryId,
                    deduplicationId,
                    adapter,
                    loop,
                    actorContext,
                    authority,
                    temporal,
                    payload,
                    redelivery,
                    scheduleExecutionDirective,
                    publicationRequested,
                    conversation,
                    visibleStatus,
                    visibleReason,
                    out envelope,
                    out validation);
            }
            else if (scheduleExecutionDirective is not null)
            {
                created = false;
                validation = Failure(
                    "schedule_execution_directive_forbidden",
                    "scheduleExecutionDirective");
            }
            else
            {
                created = TriggerDeliveryFactory.TryCreateEnvelope(
                    schemaVersion,
                    deliveryId,
                    deduplicationId,
                    kind,
                    adapter,
                    loop,
                    actorContext,
                    authority,
                    temporal,
                    payload,
                    redelivery,
                    publicationRequested,
                    conversation,
                    visibleStatus,
                    visibleReason,
                    out envelope,
                    out validation);
            }

            if (!created)
            {
                return false;
            }

            if (!TrySerializeKnownValid(envelope!, out var canonical, out _) || !string.Equals(json, canonical, StringComparison.Ordinal))
            {
                envelope = null;
                validation = Failure("noncanonical_json", "$");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            validation = Failure("invalid_json", "$");
            return false;
        }
    }

    internal static bool TrySerializeKnownValid(TriggerDeliveryEnvelope envelope, out string? json, out TriggerContractError? error)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteEnvelope(writer, envelope);
        }

        if (buffer.WrittenCount > TriggerDeliveryLimits.MaxCanonicalDocumentUtf8Bytes)
        {
            json = null;
            error = new TriggerContractError("canonical_document_too_large", "$");
            return false;
        }

        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        error = null;
        return true;
    }

    internal static bool TrySerializeLoopReferenceKnownValid(TriggerLoopReference loop, out string? json, out TriggerContractError? error)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteLoopValue(writer, loop);
            writer.Flush();
        }

        if (buffer.WrittenCount > TriggerDeliveryLimits.MaxCanonicalDocumentUtf8Bytes)
        {
            json = null;
            error = new TriggerContractError("canonical_document_too_large", "loop");
            return false;
        }

        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        error = null;
        return true;
    }

    private static void WriteEnvelope(Utf8JsonWriter writer, TriggerDeliveryEnvelope envelope)
    {
        writer.WriteStartObject();
        WriteActor(writer, envelope.ActorContext);
        WriteAdapter(writer, envelope.Adapter);
        WriteAuthority(writer, envelope.Authority);
        writer.WriteString("deduplicationId", envelope.DeduplicationId.Value);
        writer.WriteString("deliveryId", envelope.DeliveryId.Value);
        WriteConversation(writer, envelope.InvokingConversation);
        writer.WriteString("kind", TriggerVocabulary.ToCanonical(envelope.Kind));
        WriteLoop(writer, envelope.Loop);
        WritePayload(writer, envelope.Payload);
        writer.WriteBoolean("publicationRequested", envelope.PublicationRequested);
        WriteRedelivery(writer, envelope.Redelivery);
        WriteScheduleExecutionDirective(writer, envelope.ScheduleExecutionDirective);
        writer.WriteNumber("schemaVersion", envelope.SchemaVersion);
        WriteTemporal(writer, envelope.Temporal);
        writer.WriteString("visibleReason", TriggerVocabulary.ToCanonical(envelope.VisibleReason));
        writer.WriteString("visibleStatus", TriggerVocabulary.ToCanonical(envelope.VisibleStatus));
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteActor(Utf8JsonWriter writer, TriggerActorContext actor)
    {
        writer.WritePropertyName("actorContext");
        writer.WriteStartObject();
        writer.WriteString("actorId", actor.ActorId.Value);
        writer.WriteString("roleId", actor.RoleId);
        writer.WriteString("surfaceId", actor.SurfaceId);
        writer.WriteString("workspaceId", actor.WorkspaceId);
        writer.WriteEndObject();
    }

    private static void WriteAdapter(Utf8JsonWriter writer, TriggerAdapterReference adapter)
    {
        writer.WritePropertyName("adapter");
        writer.WriteStartObject();
        writer.WritePropertyName("capability");
        writer.WriteStartObject();
        writer.WriteString("hash", adapter.Capability.Hash.Value);
        writer.WriteString("id", adapter.Capability.Id.Value);
        writer.WriteString("version", adapter.Capability.Version.Value);
        writer.WriteEndObject();
        writer.WritePropertyName("implementation");
        writer.WriteStartObject();
        writer.WriteString("implementationId", adapter.Implementation.ImplementationId);
        writer.WriteString("providerId", adapter.Implementation.ProviderId.Value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteAuthority(Utf8JsonWriter writer, TriggerAuthorityEvidence authority)
    {
        writer.WritePropertyName("authority");
        writer.WriteStartObject();
        writer.WritePropertyName("boundaryReceipt");
        writer.WriteStartObject();
        writer.WritePropertyName("conditions");
        writer.WriteStartArray();
        foreach (var condition in authority.BoundaryReceipt.Conditions)
        {
            writer.WriteStartObject();
            writer.WriteString("decision", AuthorityContractVocabulary.ToCanonical(condition.Decision));
            writer.WriteString("reason", AuthorityContractVocabulary.ToCanonical(condition.Reason));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("decision", AuthorityContractVocabulary.ToCanonical(authority.BoundaryReceipt.Decision));
        WriteTimestamp(writer, "evaluatedAtUtc", authority.BoundaryReceipt.EvaluatedAtUtc);
        writer.WritePropertyName("profiles");
        writer.WriteStartArray();
        foreach (var profile in authority.BoundaryReceipt.Profiles)
        {
            WriteProfile(writer, profile);
        }

        writer.WriteEndArray();
        writer.WriteNumber("schemaVersion", authority.BoundaryReceipt.SchemaVersion);
        writer.WriteEndObject();
        writer.WritePropertyName("profile");
        WriteProfile(writer, authority.Profile);
        writer.WriteEndObject();
    }

    private static void WriteProfile(Utf8JsonWriter writer, AuthorityProfileReference profile)
    {
        writer.WriteStartObject();
        writer.WriteString("profileId", profile.ProfileId.Value);
        writer.WriteNumber("revision", profile.Revision.Value);
        writer.WriteEndObject();
    }

    private static void WriteConversation(Utf8JsonWriter writer, CustomLoopConversationReference? conversation)
    {
        writer.WritePropertyName("invokingConversation");
        if (conversation is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteTimestamp(writer, "capturedAtUtc", conversation.CapturedAtUtc);
        writer.WriteString("capturedVersion", conversation.CapturedVersion);
        writer.WriteString("conversationId", conversation.ConversationId);
        writer.WriteEndObject();
    }

    private static void WriteLoop(Utf8JsonWriter writer, TriggerLoopReference loop)
    {
        writer.WritePropertyName("loop");
        WriteLoopValue(writer, loop);
    }

    private static void WriteLoopValue(Utf8JsonWriter writer, TriggerLoopReference loop)
    {
        writer.WriteStartObject();
        WriteAuthorityGrant(writer, loop.AuthorityGrant);
        WriteGovernedPublication(writer, loop.GovernedPublication);
        writer.WriteString("kind", TriggerVocabulary.ToCanonical(loop.Kind));
        WriteLegacyDefinition(writer, loop.LegacyDefinition);
        writer.WriteEndObject();
    }

    private static void WriteAuthorityGrant(Utf8JsonWriter writer, AuthorityGrantReference? grant)
    {
        writer.WritePropertyName("authorityGrant");
        if (grant is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("contentHash", grant.ContentHash);
        writer.WriteString("grantId", grant.GrantId.Value);
        writer.WriteNumber("revision", grant.Revision.Value);
        writer.WriteEndObject();
    }

    private static void WriteGovernedPublication(Utf8JsonWriter writer, GovernedLoopRevisionPublicationPin? publication)
    {
        writer.WritePropertyName("governedPublication");
        if (publication is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("executableHash", publication.Revision.ExecutableHash);
        writer.WriteString("graphId", publication.Revision.GraphId);
        writer.WriteString("publicationOperationId", publication.PublicationOperationId);
        writer.WriteString("revisionId", publication.Revision.RevisionId);
        writer.WriteNumber("schemaVersion", publication.SchemaVersion);
        writer.WriteString("validationEvidenceHash", publication.ValidationEvidenceHash);
        writer.WriteEndObject();
    }

    private static void WriteLegacyDefinition(Utf8JsonWriter writer, TriggerLegacyLoopDefinitionReference? legacy)
    {
        writer.WritePropertyName("legacyDefinition");
        if (legacy is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("contentHash", legacy.ContentHash);
        writer.WriteNumber("definitionVersion", legacy.DefinitionVersion);
        writer.WriteString("loopId", legacy.LoopId);
        writer.WriteEndObject();
    }

    private static void WritePayload(Utf8JsonWriter writer, TriggerPayloadEvidence payload)
    {
        writer.WritePropertyName("payload");
        writer.WriteStartObject();
        writer.WriteString("contentHash", payload.ContentHash.Value);
        if (payload.GovernedReference is null)
        {
            writer.WriteNull("governedReference");
        }
        else
        {
            writer.WriteString("governedReference", payload.GovernedReference);
        }

        var bytes = payload.GetInlinePayload();
        if (bytes is null)
        {
            writer.WriteNull("inlineBase64");
        }
        else
        {
            writer.WriteBase64String("inlineBase64", bytes);
        }

        writer.WriteEndObject();
    }

    private static void WriteRedelivery(Utf8JsonWriter writer, TriggerRedeliveryEvidence redelivery)
    {
        writer.WritePropertyName("redelivery");
        writer.WriteStartObject();
        writer.WriteNumber("attempt", redelivery.Attempt);
        writer.WriteNumber("count", redelivery.Count);
        writer.WriteString("originalDeliveryId", redelivery.OriginalDeliveryId.Value);
        writer.WriteEndObject();
    }

    private static void WriteScheduleExecutionDirective(
        Utf8JsonWriter writer,
        ScheduleExecutionDirective? directive)
    {
        writer.WritePropertyName("scheduleExecutionDirective");
        if (directive is null)
        {
            writer.WriteNullValue();
            return;
        }

        WriteScheduleExecutionDirectiveValue(writer, directive);
    }

    internal static void WriteScheduleExecutionDirectiveValue(
        Utf8JsonWriter writer,
        ScheduleExecutionDirective directive)
    {
        writer.WriteStartObject();
        writer.WriteString("definitionHash", directive.DefinitionHash);
        writer.WriteNumber("definitionRevision", directive.DefinitionRevision);
        writer.WriteStartObject("identity");
        writer.WriteString("deduplicationId", directive.Identity.DeduplicationId.Value);
        writer.WriteString("deliveryId", directive.Identity.DeliveryId.Value);
        writer.WriteString("occurrenceId", directive.Identity.OccurrenceId.Value);
        writer.WriteEndObject();
        writer.WriteStartObject("occurrence");
        writer.WriteNumber("ordinal", directive.Occurrence.Ordinal);
        writer.WriteString("scheduledAtUtc", ScheduleIdentityDerivation.Utc(directive.Occurrence.ScheduledAtUtc));
        writer.WriteString("scheduledLocal", ScheduleIdentityDerivation.Local(directive.Occurrence.ScheduledLocal));
        writer.WriteNumber("schemaVersion", directive.Occurrence.SchemaVersion);
        writer.WriteStartObject("timeZone");
        writer.WriteString("rulesFingerprint", directive.Occurrence.TimeZone.RulesFingerprint);
        writer.WriteString("timeZoneId", directive.Occurrence.TimeZone.TimeZoneId);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteString("overlap", ToCanonical(directive.Overlap));
        writer.WriteString("preQueueOverlapEvidenceHash", directive.PreQueueOverlapEvidenceHash);
        writer.WriteString("scheduleId", directive.ScheduleId.Value);
        writer.WriteNumber("schemaVersion", directive.SchemaVersion);
        writer.WritePropertyName("target");
        WriteLoopValue(writer, directive.Target);
        writer.WriteEndObject();
    }

    private static void WriteTemporal(Utf8JsonWriter writer, TriggerTemporalEvidence temporal)
    {
        writer.WritePropertyName("temporal");
        writer.WriteStartObject();
        WriteNullableTimestamp(writer, "admittedAtUtc", temporal.AdmittedAtUtc);
        WriteTimestamp(writer, "createdAtUtc", temporal.CreatedAtUtc);
        WriteNullableTimestamp(writer, "deadlineUtc", temporal.DeadlineUtc);
        WriteNullableTimestamp(writer, "expiresAtUtc", temporal.ExpiresAtUtc);
        WriteNullableTimestamp(writer, "notBeforeUtc", temporal.NotBeforeUtc);
        WriteTimestamp(writer, "observedAtUtc", temporal.ObservedAtUtc);
        WriteTimestamp(writer, "receivedAtUtc", temporal.ReceivedAtUtc);
        writer.WriteEndObject();
    }

    private static bool TryAdapter(JsonElement element, out TriggerAdapterReference? adapter)
    {
        adapter = null;
        if (!IsExactObject(element, _adapterProperties))
        {
            return false;
        }

        var capabilityElement = element.GetProperty("capability");
        var implementationElement = element.GetProperty("implementation");
        if (!IsExactObject(capabilityElement, _capabilityProperties)
            || !IsExactObject(implementationElement, _implementationProperties)
            || !TryString(capabilityElement, "id", out var idText)
            || !CapabilityId.TryParse(idText, out var id, out _)
            || !TryString(capabilityElement, "version", out var versionText)
            || !CapabilityVersion.TryParse(versionText, out var version, out _)
            || !TryString(capabilityElement, "hash", out var hashText)
            || !CapabilityDescriptorHash.TryParse(hashText, out var hash, out _)
            || !TryString(implementationElement, "providerId", out var providerText)
            || !CapabilityProviderId.TryParse(providerText, out var provider, out _)
            || !TryString(implementationElement, "implementationId", out var implementationId))
        {
            return false;
        }

        adapter = new TriggerAdapterReference(new CapabilityDescriptorIdentity(id!, version!, hash!), new CapabilityImplementationIdentity(provider!, implementationId!));
        return true;
    }

    private static bool TryLoop(JsonElement element, out TriggerLoopReference? loop)
    {
        loop = null;
        if (!IsExactObject(element, _loopProperties)
            || !TryString(element, "kind", out var kindText)
            || !TriggerVocabulary.TryParseLoopTargetKind(kindText, out var kind))
        {
            return false;
        }

        var legacyElement = element.GetProperty("legacyDefinition");
        var publicationElement = element.GetProperty("governedPublication");
        var grantElement = element.GetProperty("authorityGrant");
        return kind switch
        {
            TriggerLoopTargetKind.LegacyDefinition => publicationElement.ValueKind == JsonValueKind.Null
                && grantElement.ValueKind == JsonValueKind.Null
                && TryLegacyDefinition(legacyElement, out var legacy)
                && TriggerDeliveryFactory.TryCreateLoopReference(legacy!.LoopId, legacy.DefinitionVersion, legacy.ContentHash, out loop, out _),
            TriggerLoopTargetKind.GovernedPublication => legacyElement.ValueKind == JsonValueKind.Null
                && TryGovernedPublication(publicationElement, out var publication)
                && TryAuthorityGrant(grantElement, out var grant)
                && TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grant, out loop, out _),
            _ => false
        };
    }

    private static bool TryLegacyDefinition(JsonElement element, out TriggerLegacyLoopDefinitionReference? legacy)
    {
        legacy = null;
        if (!IsExactObject(element, _legacyDefinitionProperties)
            || !TryString(element, "loopId", out var loopId)
            || !TryInteger(element, "definitionVersion", out var version)
            || !TryString(element, "contentHash", out var hash))
        {
            return false;
        }

        legacy = new TriggerLegacyLoopDefinitionReference(loopId!, version, hash!);
        return true;
    }

    private static bool TryGovernedPublication(JsonElement element, out GovernedLoopRevisionPublicationPin? publication)
    {
        publication = null;
        if (!IsExactObject(element, _governedPublicationProperties)
            || !TryInteger(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "graphId", out var graphId)
            || !TryString(element, "revisionId", out var revisionId)
            || !TryString(element, "executableHash", out var executableHash)
            || !TryString(element, "publicationOperationId", out var operationId)
            || !TryString(element, "validationEvidenceHash", out var validationHash))
        {
            return false;
        }

        try
        {
            var revision = GovernedLoopRevisionReference.Create(schemaVersion, graphId!, revisionId!, executableHash!);
            publication = GovernedLoopRevisionPublicationPinFactory.Create(schemaVersion, revision, operationId!, validationHash!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryAuthorityGrant(JsonElement element, out AuthorityGrantReference? grant)
    {
        grant = null;
        if (!IsExactObject(element, _authorityGrantProperties)
            || !TryString(element, "grantId", out var grantIdText)
            || !AuthorityGrantId.TryParse(grantIdText, out var grantId, out _)
            || !TryInteger(element, "revision", out var revisionValue)
            || !AuthorityGrantRevision.TryParse(revisionValue.ToString(CultureInfo.InvariantCulture), out var revision, out _)
            || !TryString(element, "contentHash", out var contentHash))
        {
            return false;
        }

        grant = new AuthorityGrantReference(grantId!, revision!, contentHash!);
        return true;
    }

    private static bool TryActor(JsonElement element, out TriggerActorContext? context)
    {
        context = null;
        return IsExactObject(element, _actorProperties)
            && TryString(element, "actorId", out var actorText)
            && AuthorityActorId.TryParse(actorText, out var actor, out _)
            && TryString(element, "surfaceId", out var surface)
            && TryString(element, "workspaceId", out var workspace)
            && TryString(element, "roleId", out var role)
            && TriggerDeliveryFactory.TryCreateActorContext(actor, surface, workspace, role, out context, out _);
    }

    private static bool TryAuthority(JsonElement element, out TriggerAuthorityEvidence? authority)
    {
        authority = null;
        if (!IsExactObject(element, _authorityProperties)
            || !TryProfile(element.GetProperty("profile"), out var profile)
            || !TryReceipt(element.GetProperty("boundaryReceipt"), out var receipt))
        {
            return false;
        }

        return TriggerDeliveryFactory.TryCreateAuthorityEvidence(profile, receipt, out authority);
    }

    private static bool TryReceipt(JsonElement element, out AuthorityBoundaryReceipt? receipt)
    {
        receipt = null;
        if (!IsExactObject(element, _receiptProperties)
            || !TryInteger(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "decision", out var decisionText)
            || !AuthorityContractVocabulary.TryParseDecision(decisionText, out var decision)
            || !TryTimestamp(element, "evaluatedAtUtc", out var evaluatedAtUtc))
        {
            return false;
        }

        var conditionsElement = element.GetProperty("conditions");
        var profilesElement = element.GetProperty("profiles");
        if (conditionsElement.ValueKind != JsonValueKind.Array || profilesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var conditions = new List<AuthorityBoundaryCondition>();
        foreach (var conditionElement in conditionsElement.EnumerateArray())
        {
            if (!IsExactObject(conditionElement, _conditionProperties)
                || !TryString(conditionElement, "decision", out var conditionDecisionText)
                || !AuthorityContractVocabulary.TryParseDecision(conditionDecisionText, out var conditionDecision)
                || !TryString(conditionElement, "reason", out var reasonText)
                || !AuthorityContractVocabulary.TryParseReason(reasonText, out var reason))
            {
                return false;
            }

            conditions.Add(new AuthorityBoundaryCondition(conditionDecision, reason));
        }

        var profiles = new List<AuthorityProfileReference>();
        foreach (var profileElement in profilesElement.EnumerateArray())
        {
            if (!TryProfile(profileElement, out var profile))
            {
                return false;
            }

            profiles.Add(profile!);
        }

        return AuthorityBoundaryReceiptFactory.TryCreate(schemaVersion, decision, conditions, profiles, evaluatedAtUtc, out receipt, out _);
    }

    private static bool TryProfile(JsonElement element, out AuthorityProfileReference? profile)
    {
        profile = null;
        if (!IsExactObject(element, _profileProperties)
            || !TryString(element, "profileId", out var profileText)
            || !AuthorityProfileId.TryParse(profileText, out var profileId, out _)
            || !TryInteger(element, "revision", out var revisionValue)
            || !AuthorityProfileRevision.TryParse(revisionValue.ToString(CultureInfo.InvariantCulture), out var revision, out _))
        {
            return false;
        }

        profile = new AuthorityProfileReference(profileId!, revision!);
        return true;
    }

    private static bool TryTemporal(JsonElement element, out TriggerTemporalEvidence? temporal)
    {
        temporal = null;
        return IsExactObject(element, _temporalProperties)
            && TryTimestamp(element, "createdAtUtc", out var created)
            && TryTimestamp(element, "observedAtUtc", out var observed)
            && TryTimestamp(element, "receivedAtUtc", out var received)
            && TryNullableTimestamp(element, "admittedAtUtc", out var admitted)
            && TryNullableTimestamp(element, "notBeforeUtc", out var notBefore)
            && TryNullableTimestamp(element, "deadlineUtc", out var deadline)
            && TryNullableTimestamp(element, "expiresAtUtc", out var expires)
            && TriggerDeliveryFactory.TryCreateTemporalEvidence(observed, received, created, admitted, notBefore, deadline, expires, out temporal, out _);
    }

    private static bool TryPayload(JsonElement element, out TriggerPayloadEvidence? payload)
    {
        payload = null;
        if (!IsExactObject(element, _payloadProperties)
            || !TryString(element, "contentHash", out var hashText)
            || !CapabilityIntegrityDigest.TryParse(hashText, out var hash, out _))
        {
            return false;
        }

        var inline = element.GetProperty("inlineBase64");
        var reference = element.GetProperty("governedReference");
        if (inline.ValueKind == JsonValueKind.String && reference.ValueKind == JsonValueKind.Null)
        {
            byte[] bytes;
            try
            {
                bytes = inline.GetBytesFromBase64();
            }
            catch (FormatException)
            {
                return false;
            }

            return TriggerDeliveryFactory.TryCreateInlinePayload(bytes, out payload, out _) && payload!.ContentHash.FixedTimeEquals(hash);
        }

        return inline.ValueKind == JsonValueKind.Null
            && reference.ValueKind == JsonValueKind.String
            && TriggerDeliveryFactory.TryCreateReferencedPayload(reference.GetString(), hash, out payload, out _);
    }

    private static bool TryRedelivery(JsonElement element, out TriggerRedeliveryEvidence? redelivery)
    {
        redelivery = null;
        return IsExactObject(element, _redeliveryProperties)
            && TryInteger(element, "attempt", out var attempt)
            && TryInteger(element, "count", out var count)
            && TryString(element, "originalDeliveryId", out var originalText)
            && TriggerDeliveryId.TryParse(originalText, out var original)
            && TriggerDeliveryFactory.TryCreateRedeliveryEvidence(attempt, count, original, out redelivery, out _);
    }

    private static bool TryScheduleExecutionDirective(
        JsonElement element,
        out ScheduleExecutionDirective? directive)
    {
        directive = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!IsExactObject(element, _scheduleDirectiveProperties)
            || !TryInteger(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "scheduleId", out var scheduleIdText)
            || !ScheduleId.TryParse(scheduleIdText, out var scheduleId)
            || !TryLongInteger(element, "definitionRevision", out var definitionRevision)
            || !TryString(element, "definitionHash", out var definitionHash)
            || !TryScheduleOccurrence(element.GetProperty("occurrence"), out var occurrence)
            || !TryScheduleIdentity(element.GetProperty("identity"), out var identity)
            || !TryLoop(element.GetProperty("target"), out var target)
            || !TryString(element, "overlap", out var overlapText)
            || !TryScheduleOverlap(overlapText, out var overlap)
            || !TryString(element, "preQueueOverlapEvidenceHash", out var overlapEvidenceHash))
        {
            return false;
        }

        directive = new ScheduleExecutionDirective(
            schemaVersion,
            scheduleId!,
            definitionRevision,
            definitionHash!,
            occurrence!,
            identity!,
            target!,
            overlap,
            overlapEvidenceHash!);
        return true;
    }

    private static bool TryScheduleOccurrence(JsonElement element, out ScheduleOccurrence? occurrence)
    {
        occurrence = null;
        if (!IsExactObject(element, _scheduleOccurrenceProperties)
            || !TryInteger(element, "schemaVersion", out var schemaVersion)
            || !TryLongInteger(element, "ordinal", out var ordinal)
            || !TryScheduleLocalTimestamp(element, "scheduledLocal", out var scheduledLocal)
            || !TryScheduleUtcTimestamp(element, "scheduledAtUtc", out var scheduledAtUtc)
            || !TryScheduleTimeZone(element.GetProperty("timeZone"), out var timeZone))
        {
            return false;
        }

        occurrence = new ScheduleOccurrence(
            schemaVersion,
            ordinal,
            scheduledLocal,
            scheduledAtUtc,
            timeZone!);
        return true;
    }

    private static bool TryScheduleIdentity(
        JsonElement element,
        out ScheduleOccurrenceIdentity? identity)
    {
        identity = null;
        if (!IsExactObject(element, _scheduleIdentityProperties)
            || !TryString(element, "occurrenceId", out var occurrenceIdText)
            || !ScheduleOccurrenceId.TryParse(occurrenceIdText, out var occurrenceId)
            || !TryString(element, "deliveryId", out var deliveryIdText)
            || !TriggerDeliveryId.TryParse(deliveryIdText, out var deliveryId)
            || !TryString(element, "deduplicationId", out var deduplicationIdText)
            || !TriggerDeduplicationId.TryParse(deduplicationIdText, out var deduplicationId))
        {
            return false;
        }

        identity = new ScheduleOccurrenceIdentity(occurrenceId!, deliveryId!, deduplicationId!);
        return true;
    }

    private static bool TryScheduleTimeZone(
        JsonElement element,
        out ScheduleTimeZoneReference? timeZone)
    {
        timeZone = null;
        if (!IsExactObject(element, _scheduleTimeZoneProperties)
            || !TryString(element, "timeZoneId", out var timeZoneId)
            || !TryString(element, "rulesFingerprint", out var rulesFingerprint))
        {
            return false;
        }

        timeZone = new ScheduleTimeZoneReference(timeZoneId!, rulesFingerprint!);
        return true;
    }

    private static bool TryConversation(JsonElement element, out CustomLoopConversationReference? conversation)
    {
        conversation = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!IsExactObject(element, _conversationProperties)
            || !TryString(element, "conversationId", out var conversationId)
            || !TryString(element, "capturedVersion", out var capturedVersion)
            || !TryTimestamp(element, "capturedAtUtc", out var capturedAtUtc))
        {
            return false;
        }

        conversation = new CustomLoopConversationReference(conversationId!, capturedVersion!, capturedAtUtc);
        return true;
    }

    private static bool IsExactObject(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == expected.Count && properties.Distinct(StringComparer.Ordinal).Count() == expected.Count && properties.All(expected.Contains);
    }

    private static bool TryString(JsonElement parent, string name, out string? value)
    {
        var element = parent.GetProperty(name);
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryInteger(JsonElement parent, string name, out int value)
    {
        var element = parent.GetProperty(name);
        value = default;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && string.Equals(element.GetRawText(), value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool TryLongInteger(JsonElement parent, string name, out long value)
    {
        var element = parent.GetProperty(name);
        value = default;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && string.Equals(
                element.GetRawText(),
                value.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static bool TryBoolean(JsonElement parent, string name, out bool value)
    {
        var element = parent.GetProperty(name);
        value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryTimestamp(JsonElement parent, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(parent, name, out var text)
            && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            && value.Offset == TimeSpan.Zero
            && string.Equals(text, ToCanonicalUtc(value), StringComparison.Ordinal);
    }

    private static bool TryNullableTimestamp(JsonElement parent, string name, out DateTimeOffset? value)
    {
        var element = parent.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (TryTimestamp(parent, name, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryScheduleLocalTimestamp(JsonElement parent, string name, out DateTime value)
    {
        value = default;
        return TryString(parent, name, out var text)
            && DateTime.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value)
            && value.Kind == DateTimeKind.Unspecified
            && string.Equals(text, ScheduleIdentityDerivation.Local(value), StringComparison.Ordinal);
    }

    private static bool TryScheduleUtcTimestamp(JsonElement parent, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(parent, name, out var text)
            && DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value)
            && value.Offset == TimeSpan.Zero
            && string.Equals(text, ScheduleIdentityDerivation.Utc(value), StringComparison.Ordinal);
    }

    private static bool TryScheduleOverlap(string? value, out ScheduleOverlapPolicy overlap)
    {
        overlap = value switch
        {
            "allow" => ScheduleOverlapPolicy.Allow,
            "skip" => ScheduleOverlapPolicy.Skip,
            "defer-one" => ScheduleOverlapPolicy.DeferOne,
            _ => ScheduleOverlapPolicy.Unknown,
        };
        return overlap != ScheduleOverlapPolicy.Unknown;
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset value) => writer.WriteString(name, ToCanonicalUtc(value));

    private static void WriteNullableTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            WriteTimestamp(writer, name, value.Value);
        }
    }

    private static string ToCanonicalUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string ToCanonical(ScheduleOverlapPolicy value) => value switch
    {
        ScheduleOverlapPolicy.Allow => "allow",
        ScheduleOverlapPolicy.Skip => "skip",
        ScheduleOverlapPolicy.DeferOne => "defer-one",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static TriggerContractValidationResult Failure(string code, string field) => new([new TriggerContractError(code, field)]);
}
