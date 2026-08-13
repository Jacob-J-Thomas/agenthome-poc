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
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Persistence.Triggers.Schedules.Models;

namespace EmbodySense.Core.Persistence.Triggers.Schedules;

/// <summary>Reads and writes only the exact canonical bounded schema-version-1 schedule catalog.</summary>
internal static class ScheduleStoreCodec
{
    private const int SchemaVersion = 1;
    private const int MaximumDepth = 32;
    private static readonly string[] _catalogProperties = ["entries", "generation", "schemaVersion"];
    private static readonly string[] _entryProperties = ["definition", "definitionHash", "state", "stateHash"];
    private static readonly string[] _definitionProperties = ["actorId", "authorityProfile", "daylightSaving", "enabled", "misfire", "overlap", "payload", "priority", "recurrence", "revision", "roleId", "scheduleId", "schemaVersion", "surfaceId", "target", "timeAdapter", "timeZone", "workspaceId"];
    private static readonly string[] _targetProperties = ["authorityGrant", "governedPublication", "kind"];
    private static readonly string[] _publicationProperties = ["executableHash", "graphId", "publicationOperationId", "revisionId", "schemaVersion", "validationEvidenceHash"];
    private static readonly string[] _grantProperties = ["contentHash", "grantId", "revision"];
    private static readonly string[] _adapterProperties = ["capability", "implementation"];
    private static readonly string[] _capabilityProperties = ["hash", "id", "version"];
    private static readonly string[] _implementationProperties = ["implementationId", "providerId"];
    private static readonly string[] _profileProperties = ["profileId", "revision"];
    private static readonly string[] _daylightProperties = ["ambiguousLocalTime", "invalidLocalTime"];
    private static readonly string[] _misfireProperties = ["catchUpLimit", "kind"];
    private static readonly string[] _payloadProperties = ["contentHash", "governedReference"];
    private static readonly string[] _recurrenceProperties = ["firstLocalOccurrence", "fixedIntervalSeconds", "kind"];
    private static readonly string[] _timeZoneProperties = ["rulesFingerprint", "timeZoneId"];
    private static readonly string[] _stateProperties = ["catchUpEpisode", "deferredOccurrence", "definitionHash", "definitionRevision", "dispositionEvidence", "enabled", "lastClockObservedAtUtc", "nextOccurrence", "pendingDelivery", "scheduleId", "schemaVersion", "stateRevision", "terminalDeliveryEvidence"];
    private static readonly string[] _occurrenceProperties = ["ordinal", "scheduledAtUtc", "scheduledLocal", "schemaVersion", "timeZone"];
    private static readonly string[] _catchUpProperties = ["latestDueOrdinal", "remainingAdmittedOccurrences", "schemaVersion"];
    private static readonly string[] _deferredProperties = ["deferredAtUtc", "identity", "occurrence", "schemaVersion"];
    private static readonly string[] _identityProperties = ["deduplicationId", "deliveryId", "occurrenceId"];
    private static readonly string[] _pendingProperties = ["claimId", "claimedAtUtc", "currentEvidenceHash", "finalizationPlan", "identity", "occurrence", "overlapEvidenceHash", "phase", "prepared", "recurrenceProofHash", "result", "schemaVersion"];
    private static readonly string[] _planProperties = ["catchUpEpisode", "deferredOccurrence", "dispositionEvidence", "nextOccurrence", "schemaVersion"];
    private static readonly string[] _preparedProperties = ["canonicalEnvelopeHash", "envelope", "preparedAtUtc", "schemaVersion"];
    private static readonly string[] _resultProperties = ["canonicalEnvelopeHash", "kind", "reasonCode", "recordedAtUtc", "schemaVersion"];
    private static readonly string[] _dispositionProperties = ["count", "decisionEvidenceHash", "disposition", "firstOrdinal", "firstScheduledAtUtc", "firstScheduledLocal", "lastOrdinal", "lastScheduledAtUtc", "lastScheduledLocal", "reasonCode", "recordedAtUtc", "schemaVersion", "timeZone"];
    private static readonly string[] _terminalProperties = ["currentEvidenceHash", "finalizedAtUtc", "identity", "occurrence", "overlapEvidenceHash", "recurrenceProofHash", "result", "schemaVersion"];

    public static byte[] Serialize(ScheduleStoreCatalog catalog, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != SchemaVersion || catalog.Generation < 1)
        {
            throw Invalid();
        }

        var ids = new HashSet<ScheduleId>();
        foreach (var entry in catalog.Entries)
        {
            ValidateEntry(entry);
            if (!ids.Add(entry.Definition.ScheduleId))
            {
                throw Invalid();
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (var entry in catalog.Entries)
            {
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();
            writer.WriteNumber("generation", catalog.Generation);
            writer.WriteNumber("schemaVersion", catalog.SchemaVersion);
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount > maximumBytes)
        {
            throw new ScheduleStoreCodecLimitException();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static ScheduleStoreCatalog Deserialize(byte[] bytes, int maximumSchedules, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > maximumBytes)
        {
            throw new ScheduleStoreCodecLimitException();
        }

        if (bytes.Length == 0 || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth,
            });
            var root = document.RootElement;
            if (!IsExactObject(root, _catalogProperties)
                || !TryInt32(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != SchemaVersion
                || !TryInt64(root, "generation", out var generation)
                || generation < 1)
            {
                throw Invalid();
            }

            var entriesElement = root.GetProperty("entries");
            if (entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid();
            }

            if (entriesElement.GetArrayLength() > maximumSchedules)
            {
                throw new ScheduleStoreCodecLimitException();
            }

            var entries = new List<ScheduleStoreEntry>(entriesElement.GetArrayLength());
            var ids = new HashSet<ScheduleId>();
            foreach (var element in entriesElement.EnumerateArray())
            {
                var entry = ParseEntry(element);
                if (!ids.Add(entry.Definition.ScheduleId))
                {
                    throw Invalid();
                }

                entries.Add(entry);
            }

            var catalog = new ScheduleStoreCatalog(schemaVersion, generation, entries);
            var canonical = Serialize(catalog, maximumBytes);
            if (!bytes.AsSpan().SequenceEqual(canonical))
            {
                throw Invalid();
            }

            return catalog;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException or InvalidOperationException)
        {
            throw Invalid(exception);
        }
    }

    private static ScheduleStoreEntry ParseEntry(JsonElement element)
    {
        if (!IsExactObject(element, _entryProperties)
            || !TryString(element, "definitionHash", out var definitionHash)
            || !TryString(element, "stateHash", out var stateHash))
        {
            throw Invalid();
        }

        var entry = new ScheduleStoreEntry(
            ParseDefinition(element.GetProperty("definition")),
            definitionHash!,
            ParseState(element.GetProperty("state")),
            stateHash!);
        ValidateEntry(entry);
        return entry;
    }

    private static void ValidateEntry(ScheduleStoreEntry entry)
    {
        if (entry is null
            || !ScheduleContractValidator.ValidateDefinitionStateComposition(entry.Definition, entry.State).IsValid
            || !ScheduleContractHash.TryComputeDefinition(entry.Definition, out var definitionHash, out _)
            || !string.Equals(definitionHash, entry.DefinitionHash, StringComparison.Ordinal)
            || !ScheduleContractHash.TryComputeState(entry.State, out var stateHash, out _)
            || !string.Equals(stateHash, entry.StateHash, StringComparison.Ordinal)
            || !Equals(entry.Definition.ScheduleId, entry.State.ScheduleId)
            || entry.Definition.Revision != entry.State.DefinitionRevision
            || !string.Equals(entry.DefinitionHash, entry.State.DefinitionHash, StringComparison.Ordinal))
        {
            throw Invalid();
        }
    }

    private static void WriteEntry(Utf8JsonWriter writer, ScheduleStoreEntry entry)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("definition");
        WriteDefinition(writer, entry.Definition);
        writer.WriteString("definitionHash", entry.DefinitionHash);
        writer.WritePropertyName("state");
        WriteState(writer, entry.State);
        writer.WriteString("stateHash", entry.StateHash);
        writer.WriteEndObject();
    }

    private static void WriteDefinition(Utf8JsonWriter writer, ScheduleDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteString("actorId", definition.ActorId.Value);
        writer.WritePropertyName("authorityProfile");
        WriteProfile(writer, definition.AuthorityProfile);
        writer.WriteStartObject("daylightSaving");
        writer.WriteString("ambiguousLocalTime", AmbiguousPolicy(definition.DaylightSaving.AmbiguousLocalTime));
        writer.WriteString("invalidLocalTime", InvalidPolicy(definition.DaylightSaving.InvalidLocalTime));
        writer.WriteEndObject();
        writer.WriteBoolean("enabled", definition.Enabled);
        writer.WriteStartObject("misfire");
        writer.WriteNumber("catchUpLimit", definition.Misfire.CatchUpLimit);
        writer.WriteString("kind", Misfire(definition.Misfire.Kind));
        writer.WriteEndObject();
        writer.WriteString("overlap", Overlap(definition.Overlap));
        writer.WriteStartObject("payload");
        writer.WriteString("contentHash", definition.Payload.ContentHash.Value);
        writer.WriteString("governedReference", definition.Payload.GovernedReference);
        writer.WriteEndObject();
        writer.WriteString("priority", Priority(definition.Priority));
        writer.WriteStartObject("recurrence");
        WriteLocal(writer, "firstLocalOccurrence", definition.Recurrence.FirstLocalOccurrence);
        WriteNullableInt64(writer, "fixedIntervalSeconds", definition.Recurrence.FixedIntervalSeconds);
        writer.WriteString("kind", Recurrence(definition.Recurrence.Kind));
        writer.WriteEndObject();
        writer.WriteNumber("revision", definition.Revision);
        writer.WriteString("roleId", definition.RoleId);
        writer.WriteString("scheduleId", definition.ScheduleId.Value);
        writer.WriteNumber("schemaVersion", definition.SchemaVersion);
        writer.WriteString("surfaceId", definition.SurfaceId);
        writer.WritePropertyName("target");
        WriteTarget(writer, definition.Target);
        writer.WritePropertyName("timeAdapter");
        WriteAdapter(writer, definition.TimeAdapter);
        writer.WritePropertyName("timeZone");
        WriteTimeZone(writer, definition.TimeZone);
        writer.WriteString("workspaceId", definition.WorkspaceId);
        writer.WriteEndObject();
    }

    private static ScheduleDefinition ParseDefinition(JsonElement element)
    {
        if (!IsExactObject(element, _definitionProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "scheduleId", out var scheduleText)
            || !ScheduleId.TryParse(scheduleText, out var scheduleId)
            || !TryInt64(element, "revision", out var revision)
            || !TryString(element, "actorId", out var actorText)
            || !AuthorityActorId.TryParse(actorText, out var actorId, out _)
            || !TryString(element, "surfaceId", out var surfaceId)
            || !TryString(element, "workspaceId", out var workspaceId)
            || !TryString(element, "roleId", out var roleId)
            || !TryString(element, "priority", out var priorityText)
            || !TryPriority(priorityText, out var priority)
            || !TryString(element, "overlap", out var overlapText)
            || !TryOverlap(overlapText, out var overlap)
            || !TryBoolean(element, "enabled", out var enabled))
        {
            throw Invalid();
        }

        var recurrenceElement = element.GetProperty("recurrence");
        var daylightElement = element.GetProperty("daylightSaving");
        var misfireElement = element.GetProperty("misfire");
        var payloadElement = element.GetProperty("payload");
        if (!IsExactObject(recurrenceElement, _recurrenceProperties)
            || !TryString(recurrenceElement, "kind", out var recurrenceText)
            || !TryRecurrence(recurrenceText, out var recurrenceKind)
            || !TryLocal(recurrenceElement, "firstLocalOccurrence", out var firstLocal)
            || !TryNullableInt64(recurrenceElement, "fixedIntervalSeconds", out var interval)
            || !IsExactObject(daylightElement, _daylightProperties)
            || !TryString(daylightElement, "invalidLocalTime", out var invalidText)
            || !TryInvalidPolicy(invalidText, out var invalidPolicy)
            || !TryString(daylightElement, "ambiguousLocalTime", out var ambiguousText)
            || !TryAmbiguousPolicy(ambiguousText, out var ambiguousPolicy)
            || !IsExactObject(misfireElement, _misfireProperties)
            || !TryString(misfireElement, "kind", out var misfireText)
            || !TryMisfire(misfireText, out var misfireKind)
            || !TryInt32(misfireElement, "catchUpLimit", out var catchUpLimit)
            || !IsExactObject(payloadElement, _payloadProperties)
            || !TryString(payloadElement, "governedReference", out var governedReference)
            || !TryString(payloadElement, "contentHash", out var payloadHashText)
            || !CapabilityIntegrityDigest.TryParse(payloadHashText, out var payloadHash, out _))
        {
            throw Invalid();
        }

        return new ScheduleDefinition(
            schemaVersion,
            scheduleId!,
            revision,
            ParseTarget(element.GetProperty("target")),
            ParseAdapter(element.GetProperty("timeAdapter")),
            actorId!,
            surfaceId!,
            workspaceId!,
            roleId!,
            ParseProfile(element.GetProperty("authorityProfile")),
            new SchedulePayloadReference(governedReference!, payloadHash!),
            priority,
            new ScheduleRecurrenceRule(recurrenceKind, firstLocal, interval),
            ParseTimeZone(element.GetProperty("timeZone")),
            new ScheduleDaylightSavingPolicy(invalidPolicy, ambiguousPolicy),
            new ScheduleMisfirePolicy(misfireKind, catchUpLimit),
            overlap,
            enabled);
    }

    private static void WriteTarget(Utf8JsonWriter writer, TriggerLoopReference target)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("authorityGrant");
        writer.WriteString("contentHash", target.AuthorityGrant!.ContentHash);
        writer.WriteString("grantId", target.AuthorityGrant.GrantId.Value);
        writer.WriteNumber("revision", target.AuthorityGrant.Revision.Value);
        writer.WriteEndObject();
        writer.WriteStartObject("governedPublication");
        writer.WriteString("executableHash", target.GovernedPublication!.Revision.ExecutableHash);
        writer.WriteString("graphId", target.GovernedPublication.Revision.GraphId);
        writer.WriteString("publicationOperationId", target.GovernedPublication.PublicationOperationId);
        writer.WriteString("revisionId", target.GovernedPublication.Revision.RevisionId);
        writer.WriteNumber("schemaVersion", target.GovernedPublication.SchemaVersion);
        writer.WriteString("validationEvidenceHash", target.GovernedPublication.ValidationEvidenceHash);
        writer.WriteEndObject();
        writer.WriteString("kind", "governed-publication");
        writer.WriteEndObject();
    }

    private static TriggerLoopReference ParseTarget(JsonElement element)
    {
        if (!IsExactObject(element, _targetProperties)
            || !TryString(element, "kind", out var kind)
            || !string.Equals(kind, "governed-publication", StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var publicationElement = element.GetProperty("governedPublication");
        var grantElement = element.GetProperty("authorityGrant");
        if (!IsExactObject(publicationElement, _publicationProperties)
            || !TryInt32(publicationElement, "schemaVersion", out var schemaVersion)
            || !TryString(publicationElement, "graphId", out var graphId)
            || !TryString(publicationElement, "revisionId", out var revisionId)
            || !TryString(publicationElement, "executableHash", out var executableHash)
            || !TryString(publicationElement, "publicationOperationId", out var operationId)
            || !TryString(publicationElement, "validationEvidenceHash", out var validationHash)
            || !IsExactObject(grantElement, _grantProperties)
            || !TryString(grantElement, "grantId", out var grantIdText)
            || !AuthorityGrantId.TryParse(grantIdText, out var grantId, out _)
            || !TryInt64(grantElement, "revision", out var grantRevisionValue)
            || !AuthorityGrantRevision.TryParse(grantRevisionValue.ToString(CultureInfo.InvariantCulture), out var grantRevision, out _)
            || !TryString(grantElement, "contentHash", out var grantHash))
        {
            throw Invalid();
        }

        try
        {
            var revision = GovernedLoopRevisionReference.Create(schemaVersion, graphId!, revisionId!, executableHash!);
            var publication = GovernedLoopRevisionPublicationPinFactory.Create(schemaVersion, revision, operationId!, validationHash!);
            var grant = new AuthorityGrantReference(grantId!, grantRevision!, grantHash!);
            if (!TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grant, out var target, out _))
            {
                throw Invalid();
            }

            return target!;
        }
        catch (ArgumentException exception)
        {
            throw Invalid(exception);
        }
    }

    private static void WriteAdapter(Utf8JsonWriter writer, TriggerAdapterReference adapter)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("capability");
        writer.WriteString("hash", adapter.Capability.Hash.Value);
        writer.WriteString("id", adapter.Capability.Id.Value);
        writer.WriteString("version", adapter.Capability.Version.Value);
        writer.WriteEndObject();
        writer.WriteStartObject("implementation");
        writer.WriteString("implementationId", adapter.Implementation.ImplementationId);
        writer.WriteString("providerId", adapter.Implementation.ProviderId.Value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static TriggerAdapterReference ParseAdapter(JsonElement element)
    {
        if (!IsExactObject(element, _adapterProperties))
        {
            throw Invalid();
        }

        var capabilityElement = element.GetProperty("capability");
        var implementationElement = element.GetProperty("implementation");
        if (!IsExactObject(capabilityElement, _capabilityProperties)
            || !TryString(capabilityElement, "id", out var idText)
            || !CapabilityId.TryParse(idText, out var id, out _)
            || !TryString(capabilityElement, "version", out var versionText)
            || !CapabilityVersion.TryParse(versionText, out var version, out _)
            || !TryString(capabilityElement, "hash", out var hashText)
            || !CapabilityDescriptorHash.TryParse(hashText, out var hash, out _)
            || !IsExactObject(implementationElement, _implementationProperties)
            || !TryString(implementationElement, "providerId", out var providerText)
            || !CapabilityProviderId.TryParse(providerText, out var provider, out _)
            || !TryString(implementationElement, "implementationId", out var implementationId))
        {
            throw Invalid();
        }

        return new TriggerAdapterReference(
            new CapabilityDescriptorIdentity(id!, version!, hash!),
            new CapabilityImplementationIdentity(provider!, implementationId!));
    }

    private static void WriteProfile(Utf8JsonWriter writer, AuthorityProfileReference profile)
    {
        writer.WriteStartObject();
        writer.WriteString("profileId", profile.ProfileId.Value);
        writer.WriteNumber("revision", profile.Revision.Value);
        writer.WriteEndObject();
    }

    private static AuthorityProfileReference ParseProfile(JsonElement element)
    {
        if (!IsExactObject(element, _profileProperties)
            || !TryString(element, "profileId", out var idText)
            || !AuthorityProfileId.TryParse(idText, out var id, out _)
            || !TryInt64(element, "revision", out var revisionValue)
            || !AuthorityProfileRevision.TryParse(revisionValue.ToString(CultureInfo.InvariantCulture), out var revision, out _))
        {
            throw Invalid();
        }

        return new AuthorityProfileReference(id!, revision!);
    }

    private static void WriteState(Utf8JsonWriter writer, ScheduleState state)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("catchUpEpisode");
        WriteCatchUp(writer, state.CatchUpEpisode);
        writer.WritePropertyName("deferredOccurrence");
        WriteDeferred(writer, state.DeferredOccurrence);
        writer.WriteString("definitionHash", state.DefinitionHash);
        writer.WriteNumber("definitionRevision", state.DefinitionRevision);
        writer.WriteStartArray("dispositionEvidence");
        foreach (var evidence in state.DispositionEvidence)
        {
            WriteDisposition(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("enabled", state.Enabled);
        WriteNullableUtc(writer, "lastClockObservedAtUtc", state.LastClockObservedAtUtc);
        writer.WritePropertyName("nextOccurrence");
        WriteOccurrence(writer, state.NextOccurrence);
        writer.WritePropertyName("pendingDelivery");
        WritePending(writer, state.PendingDelivery);
        writer.WriteString("scheduleId", state.ScheduleId.Value);
        writer.WriteNumber("schemaVersion", state.SchemaVersion);
        writer.WriteNumber("stateRevision", state.StateRevision);
        writer.WriteStartArray("terminalDeliveryEvidence");
        foreach (var evidence in state.TerminalDeliveryEvidence)
        {
            WriteTerminal(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static ScheduleState ParseState(JsonElement element)
    {
        if (!IsExactObject(element, _stateProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "scheduleId", out var scheduleText)
            || !ScheduleId.TryParse(scheduleText, out var scheduleId)
            || !TryInt64(element, "definitionRevision", out var definitionRevision)
            || !TryString(element, "definitionHash", out var definitionHash)
            || !TryInt64(element, "stateRevision", out var stateRevision)
            || !TryBoolean(element, "enabled", out var enabled)
            || !TryNullableUtc(element, "lastClockObservedAtUtc", out var lastClock))
        {
            throw Invalid();
        }

        return new ScheduleState(
            schemaVersion,
            scheduleId!,
            definitionRevision,
            definitionHash!,
            stateRevision,
            enabled,
            ParseNullableOccurrence(element.GetProperty("nextOccurrence")),
            ParseNullableCatchUp(element.GetProperty("catchUpEpisode")),
            ParseNullableDeferred(element.GetProperty("deferredOccurrence")),
            lastClock,
            ParseNullablePending(element.GetProperty("pendingDelivery")),
            ParseDispositionArray(element.GetProperty("dispositionEvidence"), ScheduleContractLimits.MaxDispositionEvidenceItems),
            ParseTerminalArray(element.GetProperty("terminalDeliveryEvidence")));
    }

    private static void WriteOccurrence(Utf8JsonWriter writer, ScheduleOccurrence? occurrence)
    {
        if (occurrence is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("ordinal", occurrence.Ordinal);
        WriteUtc(writer, "scheduledAtUtc", occurrence.ScheduledAtUtc);
        WriteLocal(writer, "scheduledLocal", occurrence.ScheduledLocal);
        writer.WriteNumber("schemaVersion", occurrence.SchemaVersion);
        writer.WritePropertyName("timeZone");
        WriteTimeZone(writer, occurrence.TimeZone);
        writer.WriteEndObject();
    }

    private static ScheduleOccurrence? ParseNullableOccurrence(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : ParseOccurrence(element);

    private static ScheduleOccurrence ParseOccurrence(JsonElement element)
    {
        if (!IsExactObject(element, _occurrenceProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryInt64(element, "ordinal", out var ordinal)
            || !TryLocal(element, "scheduledLocal", out var scheduledLocal)
            || !TryUtc(element, "scheduledAtUtc", out var scheduledAtUtc))
        {
            throw Invalid();
        }

        return new ScheduleOccurrence(schemaVersion, ordinal, scheduledLocal, scheduledAtUtc, ParseTimeZone(element.GetProperty("timeZone")));
    }

    private static void WriteTimeZone(Utf8JsonWriter writer, ScheduleTimeZoneReference timeZone)
    {
        writer.WriteStartObject();
        writer.WriteString("rulesFingerprint", timeZone.RulesFingerprint);
        writer.WriteString("timeZoneId", timeZone.TimeZoneId);
        writer.WriteEndObject();
    }

    private static ScheduleTimeZoneReference ParseTimeZone(JsonElement element)
    {
        if (!IsExactObject(element, _timeZoneProperties)
            || !TryString(element, "timeZoneId", out var id)
            || !TryString(element, "rulesFingerprint", out var fingerprint))
        {
            throw Invalid();
        }

        return new ScheduleTimeZoneReference(id!, fingerprint!);
    }

    private static void WriteCatchUp(Utf8JsonWriter writer, ScheduleCatchUpEpisode? episode)
    {
        if (episode is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("latestDueOrdinal", episode.LatestDueOrdinal);
        writer.WriteNumber("remainingAdmittedOccurrences", episode.RemainingAdmittedOccurrences);
        writer.WriteNumber("schemaVersion", episode.SchemaVersion);
        writer.WriteEndObject();
    }

    private static ScheduleCatchUpEpisode? ParseNullableCatchUp(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!IsExactObject(element, _catchUpProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryInt64(element, "latestDueOrdinal", out var latestDueOrdinal)
            || !TryInt32(element, "remainingAdmittedOccurrences", out var remaining))
        {
            throw Invalid();
        }

        return new ScheduleCatchUpEpisode(schemaVersion, latestDueOrdinal, remaining);
    }

    private static void WriteDeferred(Utf8JsonWriter writer, ScheduleDeferredOccurrence? deferred)
    {
        if (deferred is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteUtc(writer, "deferredAtUtc", deferred.DeferredAtUtc);
        writer.WritePropertyName("identity");
        WriteIdentity(writer, deferred.Identity);
        writer.WritePropertyName("occurrence");
        WriteOccurrence(writer, deferred.Occurrence);
        writer.WriteNumber("schemaVersion", deferred.SchemaVersion);
        writer.WriteEndObject();
    }

    private static ScheduleDeferredOccurrence? ParseNullableDeferred(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!IsExactObject(element, _deferredProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryUtc(element, "deferredAtUtc", out var deferredAtUtc))
        {
            throw Invalid();
        }

        return new ScheduleDeferredOccurrence(
            schemaVersion,
            ParseOccurrence(element.GetProperty("occurrence")),
            ParseIdentity(element.GetProperty("identity")),
            deferredAtUtc);
    }

    private static void WriteIdentity(Utf8JsonWriter writer, ScheduleOccurrenceIdentity identity)
    {
        writer.WriteStartObject();
        writer.WriteString("deduplicationId", identity.DeduplicationId.Value);
        writer.WriteString("deliveryId", identity.DeliveryId.Value);
        writer.WriteString("occurrenceId", identity.OccurrenceId.Value);
        writer.WriteEndObject();
    }

    private static ScheduleOccurrenceIdentity ParseIdentity(JsonElement element)
    {
        if (!IsExactObject(element, _identityProperties)
            || !TryString(element, "occurrenceId", out var occurrenceText)
            || !ScheduleOccurrenceId.TryParse(occurrenceText, out var occurrenceId)
            || !TryString(element, "deliveryId", out var deliveryText)
            || !TriggerDeliveryId.TryParse(deliveryText, out var deliveryId)
            || !TryString(element, "deduplicationId", out var deduplicationText)
            || !TriggerDeduplicationId.TryParse(deduplicationText, out var deduplicationId))
        {
            throw Invalid();
        }

        return new ScheduleOccurrenceIdentity(occurrenceId!, deliveryId!, deduplicationId!);
    }

    private static void WritePending(Utf8JsonWriter writer, SchedulePendingDelivery? pending)
    {
        if (pending is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("claimId", pending.ClaimId.Value);
        WriteUtc(writer, "claimedAtUtc", pending.ClaimedAtUtc);
        WriteNullableString(writer, "currentEvidenceHash", pending.CurrentEvidenceHash);
        writer.WritePropertyName("finalizationPlan");
        WritePlan(writer, pending.FinalizationPlan);
        writer.WritePropertyName("identity");
        WriteIdentity(writer, pending.Identity);
        writer.WritePropertyName("occurrence");
        WriteOccurrence(writer, pending.Occurrence);
        WriteNullableString(writer, "overlapEvidenceHash", pending.OverlapEvidenceHash);
        writer.WriteString("phase", PendingPhase(pending.Phase));
        writer.WritePropertyName("prepared");
        WritePrepared(writer, pending.Prepared);
        WriteNullableString(writer, "recurrenceProofHash", pending.RecurrenceProofHash);
        writer.WritePropertyName("result");
        WriteResult(writer, pending.Result);
        writer.WriteNumber("schemaVersion", pending.SchemaVersion);
        writer.WriteEndObject();
    }

    private static SchedulePendingDelivery? ParseNullablePending(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!IsExactObject(element, _pendingProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "phase", out var phaseText)
            || !TryPendingPhase(phaseText, out var phase)
            || !TryString(element, "claimId", out var claimText)
            || !ScheduleClaimId.TryParse(claimText, out var claimId)
            || !TryUtc(element, "claimedAtUtc", out var claimedAtUtc)
            || !TryNullableString(element, "currentEvidenceHash", out var currentEvidenceHash)
            || !TryNullableString(element, "overlapEvidenceHash", out var overlapEvidenceHash)
            || !TryNullableString(element, "recurrenceProofHash", out var recurrenceProofHash))
        {
            throw Invalid();
        }

        return new SchedulePendingDelivery(
            schemaVersion,
            phase,
            ParseOccurrence(element.GetProperty("occurrence")),
            ParseIdentity(element.GetProperty("identity")),
            claimId!,
            claimedAtUtc,
            currentEvidenceHash,
            recurrenceProofHash,
            overlapEvidenceHash,
            ParseNullablePlan(element.GetProperty("finalizationPlan")),
            ParseNullablePrepared(element.GetProperty("prepared")),
            ParseNullableResult(element.GetProperty("result")));
    }

    private static void WritePlan(Utf8JsonWriter writer, ScheduleFinalizationPlan? plan)
    {
        if (plan is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("catchUpEpisode");
        WriteCatchUp(writer, plan.CatchUpEpisode);
        writer.WritePropertyName("deferredOccurrence");
        WriteDeferred(writer, plan.DeferredOccurrence);
        writer.WriteStartArray("dispositionEvidence");
        foreach (var evidence in plan.DispositionEvidence)
        {
            WriteDisposition(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("nextOccurrence");
        WriteOccurrence(writer, plan.NextOccurrence);
        writer.WriteNumber("schemaVersion", plan.SchemaVersion);
        writer.WriteEndObject();
    }

    private static ScheduleFinalizationPlan? ParseNullablePlan(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!IsExactObject(element, _planProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion))
        {
            throw Invalid();
        }

        return new ScheduleFinalizationPlan(
            schemaVersion,
            ParseNullableOccurrence(element.GetProperty("nextOccurrence")),
            ParseNullableCatchUp(element.GetProperty("catchUpEpisode")),
            ParseNullableDeferred(element.GetProperty("deferredOccurrence")),
            ParseDispositionArray(element.GetProperty("dispositionEvidence"), ScheduleContractLimits.MaxFinalizationEvidenceItems));
    }

    private static void WritePrepared(Utf8JsonWriter writer, SchedulePreparedDelivery? prepared)
    {
        if (prepared is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (!TriggerDeliveryJson.TrySerialize(prepared.Envelope, out var envelopeJson, out _))
        {
            throw Invalid();
        }

        writer.WriteStartObject();
        writer.WriteString("canonicalEnvelopeHash", prepared.CanonicalEnvelopeHash);
        writer.WritePropertyName("envelope");
        writer.WriteRawValue(envelopeJson!);
        WriteUtc(writer, "preparedAtUtc", prepared.PreparedAtUtc);
        writer.WriteNumber("schemaVersion", prepared.SchemaVersion);
        writer.WriteEndObject();
    }

    private static SchedulePreparedDelivery? ParseNullablePrepared(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!IsExactObject(element, _preparedProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "canonicalEnvelopeHash", out var envelopeHash)
            || !TryUtc(element, "preparedAtUtc", out var preparedAtUtc)
            || !TriggerDeliveryJson.TryDeserialize(element.GetProperty("envelope").GetRawText(), out var envelope, out _))
        {
            throw Invalid();
        }

        return new SchedulePreparedDelivery(schemaVersion, envelope!, envelopeHash!, preparedAtUtc);
    }

    private static void WriteResult(Utf8JsonWriter writer, ScheduleDeliveryResultEvidence? result)
    {
        if (result is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("canonicalEnvelopeHash", result.CanonicalEnvelopeHash);
        writer.WriteString("kind", DeliveryResult(result.Kind));
        writer.WriteString("reasonCode", result.ReasonCode);
        WriteUtc(writer, "recordedAtUtc", result.RecordedAtUtc);
        writer.WriteNumber("schemaVersion", result.SchemaVersion);
        writer.WriteEndObject();
    }

    private static ScheduleDeliveryResultEvidence? ParseNullableResult(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!IsExactObject(element, _resultProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "kind", out var kindText)
            || !TryDeliveryResult(kindText, out var kind)
            || !TryString(element, "reasonCode", out var reasonCode)
            || !TryString(element, "canonicalEnvelopeHash", out var envelopeHash)
            || !TryUtc(element, "recordedAtUtc", out var recordedAtUtc))
        {
            throw Invalid();
        }

        return new ScheduleDeliveryResultEvidence(schemaVersion, kind, reasonCode!, envelopeHash!, recordedAtUtc);
    }

    private static void WriteDisposition(Utf8JsonWriter writer, ScheduleOccurrenceDispositionEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WriteNumber("count", evidence.Count);
        WriteNullableString(writer, "decisionEvidenceHash", evidence.DecisionEvidenceHash);
        writer.WriteString("disposition", Disposition(evidence.Disposition));
        writer.WriteNumber("firstOrdinal", evidence.FirstOrdinal);
        WriteNullableUtc(writer, "firstScheduledAtUtc", evidence.FirstScheduledAtUtc);
        WriteLocal(writer, "firstScheduledLocal", evidence.FirstScheduledLocal);
        writer.WriteNumber("lastOrdinal", evidence.LastOrdinal);
        WriteNullableUtc(writer, "lastScheduledAtUtc", evidence.LastScheduledAtUtc);
        WriteLocal(writer, "lastScheduledLocal", evidence.LastScheduledLocal);
        writer.WriteString("reasonCode", evidence.ReasonCode);
        WriteUtc(writer, "recordedAtUtc", evidence.RecordedAtUtc);
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WritePropertyName("timeZone");
        WriteTimeZone(writer, evidence.TimeZone);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<ScheduleOccurrenceDispositionEvidence> ParseDispositionArray(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        if (element.GetArrayLength() > maximum)
        {
            throw new ScheduleStoreCodecLimitException();
        }

        return element.EnumerateArray().Select(ParseDisposition).ToArray();
    }

    private static ScheduleOccurrenceDispositionEvidence ParseDisposition(JsonElement element)
    {
        if (!IsExactObject(element, _dispositionProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryInt64(element, "firstOrdinal", out var firstOrdinal)
            || !TryInt64(element, "lastOrdinal", out var lastOrdinal)
            || !TryInt64(element, "count", out var count)
            || !TryLocal(element, "firstScheduledLocal", out var firstLocal)
            || !TryLocal(element, "lastScheduledLocal", out var lastLocal)
            || !TryNullableUtc(element, "firstScheduledAtUtc", out var firstUtc)
            || !TryNullableUtc(element, "lastScheduledAtUtc", out var lastUtc)
            || !TryNullableString(element, "decisionEvidenceHash", out var decisionEvidenceHash)
            || !TryString(element, "disposition", out var dispositionText)
            || !TryDisposition(dispositionText, out var disposition)
            || !TryString(element, "reasonCode", out var reasonCode)
            || !TryUtc(element, "recordedAtUtc", out var recordedAtUtc))
        {
            throw Invalid();
        }

        return new ScheduleOccurrenceDispositionEvidence(
            schemaVersion,
            firstOrdinal,
            lastOrdinal,
            count,
            firstLocal,
            lastLocal,
            firstUtc,
            lastUtc,
            ParseTimeZone(element.GetProperty("timeZone")),
            disposition,
            decisionEvidenceHash,
            reasonCode!,
            recordedAtUtc);
    }

    private static void WriteTerminal(Utf8JsonWriter writer, ScheduleTerminalDeliveryEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WriteString("currentEvidenceHash", evidence.CurrentEvidenceHash);
        WriteUtc(writer, "finalizedAtUtc", evidence.FinalizedAtUtc);
        writer.WritePropertyName("identity");
        WriteIdentity(writer, evidence.Identity);
        writer.WritePropertyName("occurrence");
        WriteOccurrence(writer, evidence.Occurrence);
        writer.WriteString("overlapEvidenceHash", evidence.OverlapEvidenceHash);
        writer.WriteString("recurrenceProofHash", evidence.RecurrenceProofHash);
        writer.WritePropertyName("result");
        WriteResult(writer, evidence.Result);
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<ScheduleTerminalDeliveryEvidence> ParseTerminalArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        if (element.GetArrayLength() > ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems)
        {
            throw new ScheduleStoreCodecLimitException();
        }

        return element.EnumerateArray().Select(ParseTerminal).ToArray();
    }

    private static ScheduleTerminalDeliveryEvidence ParseTerminal(JsonElement element)
    {
        if (!IsExactObject(element, _terminalProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "currentEvidenceHash", out var currentEvidenceHash)
            || !TryString(element, "recurrenceProofHash", out var recurrenceProofHash)
            || !TryString(element, "overlapEvidenceHash", out var overlapEvidenceHash)
            || !TryUtc(element, "finalizedAtUtc", out var finalizedAtUtc))
        {
            throw Invalid();
        }

        return new ScheduleTerminalDeliveryEvidence(
            schemaVersion,
            ParseOccurrence(element.GetProperty("occurrence")),
            ParseIdentity(element.GetProperty("identity")),
            currentEvidenceHash!,
            recurrenceProofHash!,
            overlapEvidenceHash!,
            ParseNullableResult(element.GetProperty("result")) ?? throw Invalid(),
            finalizedAtUtc);
    }

    private static bool IsExactObject(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == expected.Count
            && properties.Distinct(StringComparer.Ordinal).Count() == expected.Count
            && properties.All(expected.Contains);
    }

    private static bool TryString(JsonElement parent, string name, out string? value)
    {
        var element = parent.GetProperty(name);
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryNullableString(JsonElement parent, string name, out string? value)
    {
        var element = parent.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        return TryString(parent, name, out value);
    }

    private static bool TryBoolean(JsonElement parent, string name, out bool value)
    {
        var element = parent.GetProperty(name);
        value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryInt32(JsonElement parent, string name, out int value)
    {
        var element = parent.GetProperty(name);
        value = default;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && string.Equals(element.GetRawText(), value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool TryInt64(JsonElement parent, string name, out long value)
    {
        var element = parent.GetProperty(name);
        value = default;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && string.Equals(element.GetRawText(), value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool TryNullableInt64(JsonElement parent, string name, out long? value)
    {
        var element = parent.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (TryInt64(parent, name, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static void WriteNullableInt64(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteLocal(Utf8JsonWriter writer, string name, DateTime value)
        => writer.WriteString(name, value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));

    private static bool TryLocal(JsonElement parent, string name, out DateTime value)
    {
        value = default;
        return TryString(parent, name, out var text)
            && DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            && value.Kind == DateTimeKind.Unspecified
            && string.Equals(text, value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static void WriteUtc(Utf8JsonWriter writer, string name, DateTimeOffset value)
        => writer.WriteString(name, value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));

    private static void WriteNullableUtc(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            WriteUtc(writer, name, value.Value);
        }
    }

    private static bool TryUtc(JsonElement parent, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(parent, name, out var text)
            && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)
            && value.Offset == TimeSpan.Zero
            && string.Equals(text, value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool TryNullableUtc(JsonElement parent, string name, out DateTimeOffset? value)
    {
        var element = parent.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (TryUtc(parent, name, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static string Priority(SchedulePriority value) => value switch
    {
        SchedulePriority.Background => "background",
        SchedulePriority.Normal => "normal",
        SchedulePriority.Elevated => "elevated",
        SchedulePriority.Critical => "critical",
        _ => throw Invalid(),
    };

    private static bool TryPriority(string? value, out SchedulePriority result)
        => TryToken(value, out result, ("background", SchedulePriority.Background), ("normal", SchedulePriority.Normal), ("elevated", SchedulePriority.Elevated), ("critical", SchedulePriority.Critical));

    private static string Recurrence(ScheduleRecurrenceKind value) => value switch
    {
        ScheduleRecurrenceKind.Once => "once",
        ScheduleRecurrenceKind.FixedInterval => "fixed-interval",
        ScheduleRecurrenceKind.Daily => "daily",
        ScheduleRecurrenceKind.Weekly => "weekly",
        _ => throw Invalid(),
    };

    private static bool TryRecurrence(string? value, out ScheduleRecurrenceKind result)
        => TryToken(value, out result, ("once", ScheduleRecurrenceKind.Once), ("fixed-interval", ScheduleRecurrenceKind.FixedInterval), ("daily", ScheduleRecurrenceKind.Daily), ("weekly", ScheduleRecurrenceKind.Weekly));

    private static string InvalidPolicy(ScheduleInvalidLocalTimePolicy value) => value switch
    {
        ScheduleInvalidLocalTimePolicy.Skip => "skip",
        ScheduleInvalidLocalTimePolicy.ShiftForward => "shift-forward",
        _ => throw Invalid(),
    };

    private static bool TryInvalidPolicy(string? value, out ScheduleInvalidLocalTimePolicy result)
        => TryToken(value, out result, ("skip", ScheduleInvalidLocalTimePolicy.Skip), ("shift-forward", ScheduleInvalidLocalTimePolicy.ShiftForward));

    private static string AmbiguousPolicy(ScheduleAmbiguousLocalTimePolicy value) => value switch
    {
        ScheduleAmbiguousLocalTimePolicy.EarlierUtc => "earlier-utc",
        ScheduleAmbiguousLocalTimePolicy.LaterUtc => "later-utc",
        _ => throw Invalid(),
    };

    private static bool TryAmbiguousPolicy(string? value, out ScheduleAmbiguousLocalTimePolicy result)
        => TryToken(value, out result, ("earlier-utc", ScheduleAmbiguousLocalTimePolicy.EarlierUtc), ("later-utc", ScheduleAmbiguousLocalTimePolicy.LaterUtc));

    private static string Misfire(ScheduleMisfirePolicyKind value) => value switch
    {
        ScheduleMisfirePolicyKind.Skip => "skip",
        ScheduleMisfirePolicyKind.FireLatestOnce => "fire-latest-once",
        ScheduleMisfirePolicyKind.CatchUp => "catch-up",
        _ => throw Invalid(),
    };

    private static bool TryMisfire(string? value, out ScheduleMisfirePolicyKind result)
        => TryToken(value, out result, ("skip", ScheduleMisfirePolicyKind.Skip), ("fire-latest-once", ScheduleMisfirePolicyKind.FireLatestOnce), ("catch-up", ScheduleMisfirePolicyKind.CatchUp));

    private static string Overlap(ScheduleOverlapPolicy value) => value switch
    {
        ScheduleOverlapPolicy.Allow => "allow",
        ScheduleOverlapPolicy.Skip => "skip",
        ScheduleOverlapPolicy.DeferOne => "defer-one",
        _ => throw Invalid(),
    };

    private static bool TryOverlap(string? value, out ScheduleOverlapPolicy result)
        => TryToken(value, out result, ("allow", ScheduleOverlapPolicy.Allow), ("skip", ScheduleOverlapPolicy.Skip), ("defer-one", ScheduleOverlapPolicy.DeferOne));

    private static string PendingPhase(SchedulePendingDeliveryPhase value) => value switch
    {
        SchedulePendingDeliveryPhase.Claimed => "claimed",
        SchedulePendingDeliveryPhase.Prepared => "prepared",
        SchedulePendingDeliveryPhase.ResultObserved => "result-observed",
        _ => throw Invalid(),
    };

    private static bool TryPendingPhase(string? value, out SchedulePendingDeliveryPhase result)
        => TryToken(value, out result, ("claimed", SchedulePendingDeliveryPhase.Claimed), ("prepared", SchedulePendingDeliveryPhase.Prepared), ("result-observed", SchedulePendingDeliveryPhase.ResultObserved));

    private static string DeliveryResult(ScheduleDeliveryResultKind value) => value switch
    {
        ScheduleDeliveryResultKind.Queued => "queued",
        ScheduleDeliveryResultKind.Replayed => "replayed",
        ScheduleDeliveryResultKind.Rejected => "rejected",
        ScheduleDeliveryResultKind.Backpressured => "backpressured",
        ScheduleDeliveryResultKind.Unavailable => "unavailable",
        ScheduleDeliveryResultKind.Ambiguous => "ambiguous",
        _ => throw Invalid(),
    };

    private static bool TryDeliveryResult(string? value, out ScheduleDeliveryResultKind result)
        => TryToken(value, out result, ("queued", ScheduleDeliveryResultKind.Queued), ("replayed", ScheduleDeliveryResultKind.Replayed), ("rejected", ScheduleDeliveryResultKind.Rejected), ("backpressured", ScheduleDeliveryResultKind.Backpressured), ("unavailable", ScheduleDeliveryResultKind.Unavailable), ("ambiguous", ScheduleDeliveryResultKind.Ambiguous));

    private static string Disposition(ScheduleOccurrenceDisposition value) => value switch
    {
        ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped => "invalid-local-time-skipped",
        ScheduleOccurrenceDisposition.MisfireSkipped => "misfire-skipped",
        ScheduleOccurrenceDisposition.OverlapSkipped => "overlap-skipped",
        ScheduleOccurrenceDisposition.OverlapDeferred => "overlap-deferred",
        _ => throw Invalid(),
    };

    private static bool TryDisposition(string? value, out ScheduleOccurrenceDisposition result)
        => TryToken(value, out result, ("invalid-local-time-skipped", ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped), ("misfire-skipped", ScheduleOccurrenceDisposition.MisfireSkipped), ("overlap-skipped", ScheduleOccurrenceDisposition.OverlapSkipped), ("overlap-deferred", ScheduleOccurrenceDisposition.OverlapDeferred));

    private static bool TryToken<T>(string? value, out T result, params (string Token, T Value)[] candidates)
        where T : struct, Enum
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(value, candidate.Token, StringComparison.Ordinal))
            {
                result = candidate.Value;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static FormatException Invalid(Exception? inner = null)
        => new("The schedule catalog is not exact canonical schema-version-1 data.", inner);
}
