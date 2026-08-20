using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Computes stable lowercase SHA-256 identities from canonical schema-1 schedule contracts.</summary>
public static class ScheduleContractHash
{
    /// <summary>Validates and hashes one complete immutable schedule execution directive.</summary>
    public static bool TryComputeExecutionDirective(
        ScheduleExecutionDirective? directive,
        out string? hash,
        out ScheduleContractValidationResult validation)
    {
        validation = ScheduleContractValidator.ValidateExecutionDirective(directive);
        if (!validation.IsValid)
        {
            hash = null;
            return false;
        }

        return TryHash(
            writer => TriggerDeliveryJson.WriteScheduleExecutionDirectiveValue(writer, directive!),
            out hash,
            out validation);
    }

    /// <summary>Validates and hashes the complete immutable schedule definition.</summary>
    public static bool TryComputeDefinition(
        ScheduleDefinition? definition,
        out string? hash,
        out ScheduleContractValidationResult validation)
    {
        validation = ScheduleContractValidator.ValidateDefinition(definition);
        if (!validation.IsValid)
        {
            hash = null;
            return false;
        }

        return TryHash(writer => WriteDefinition(writer, definition!), out hash, out validation);
    }

    /// <summary>Validates and hashes the complete optimistic schedule-state snapshot.</summary>
    public static bool TryComputeState(
        ScheduleState? state,
        out string? hash,
        out ScheduleContractValidationResult validation)
    {
        validation = ScheduleContractValidator.ValidateState(state);
        if (!validation.IsValid)
        {
            hash = null;
            return false;
        }

        return TryHash(writer => WriteState(writer, state!), out hash, out validation);
    }

    private static bool TryHash(
        Action<Utf8JsonWriter> write,
        out string? hash,
        out ScheduleContractValidationResult validation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }

        if (buffer.WrittenCount > ScheduleContractLimits.MaxCanonicalDocumentUtf8Bytes)
        {
            hash = null;
            validation = new ScheduleContractValidationResult(
                [new ScheduleContractError("canonical_document_too_large", "$")]);
            return false;
        }

        hash = Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
        validation = new ScheduleContractValidationResult([]);
        return true;
    }

    private static void WriteDefinition(Utf8JsonWriter writer, ScheduleDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", definition.SchemaVersion);
        writer.WriteString("scheduleId", definition.ScheduleId.Value);
        writer.WriteNumber("revision", definition.Revision);

        writer.WriteStartObject("target");
        writer.WriteString("kind", "governed-publication");
        writer.WriteStartObject("publication");
        writer.WriteNumber("schemaVersion", definition.Target.GovernedPublication!.SchemaVersion);
        writer.WriteString("graphId", definition.Target.GovernedPublication.Revision.GraphId);
        writer.WriteString("revisionId", definition.Target.GovernedPublication.Revision.RevisionId);
        writer.WriteString("executableHash", definition.Target.GovernedPublication.Revision.ExecutableHash);
        writer.WriteString("publicationOperationId", definition.Target.GovernedPublication.PublicationOperationId);
        writer.WriteString("validationEvidenceHash", definition.Target.GovernedPublication.ValidationEvidenceHash);
        writer.WriteEndObject();
        writer.WriteStartObject("authorityGrant");
        writer.WriteString("grantId", definition.Target.AuthorityGrant!.GrantId.Value);
        writer.WriteNumber("revision", definition.Target.AuthorityGrant.Revision.Value);
        writer.WriteString("contentHash", definition.Target.AuthorityGrant.ContentHash);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject("timeAdapter");
        writer.WriteStartObject("capability");
        writer.WriteString("id", definition.TimeAdapter.Capability.Id.Value);
        writer.WriteString("version", definition.TimeAdapter.Capability.Version.Value);
        writer.WriteString("hash", definition.TimeAdapter.Capability.Hash.Value);
        writer.WriteEndObject();
        writer.WriteStartObject("implementation");
        writer.WriteString("providerId", definition.TimeAdapter.Implementation.ProviderId.Value);
        writer.WriteString("implementationId", definition.TimeAdapter.Implementation.ImplementationId);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteString("actorId", definition.ActorId.Value);
        writer.WriteString("surfaceId", definition.SurfaceId);
        writer.WriteString("workspaceId", definition.WorkspaceId);
        writer.WriteString("roleId", definition.RoleId);
        writer.WriteStartObject("authorityProfile");
        writer.WriteString("profileId", definition.AuthorityProfile.ProfileId.Value);
        writer.WriteNumber("revision", definition.AuthorityProfile.Revision.Value);
        writer.WriteEndObject();
        writer.WriteStartObject("payload");
        writer.WriteString("governedReference", definition.Payload.GovernedReference);
        writer.WriteString("contentHash", definition.Payload.ContentHash.Value);
        writer.WriteEndObject();
        writer.WriteString("priority", Priority(definition.Priority));

        writer.WriteStartObject("recurrence");
        writer.WriteString("kind", Recurrence(definition.Recurrence.Kind));
        writer.WriteString("firstLocalOccurrence", ScheduleIdentityDerivation.Local(definition.Recurrence.FirstLocalOccurrence));
        WriteNullableNumber(writer, "fixedIntervalSeconds", definition.Recurrence.FixedIntervalSeconds);
        writer.WriteEndObject();

        WriteTimeZone(writer, "timeZone", definition.TimeZone);
        writer.WriteStartObject("daylightSaving");
        writer.WriteString("invalidLocalTime", InvalidLocalTime(definition.DaylightSaving.InvalidLocalTime));
        writer.WriteString("ambiguousLocalTime", AmbiguousLocalTime(definition.DaylightSaving.AmbiguousLocalTime));
        writer.WriteEndObject();
        writer.WriteStartObject("misfire");
        writer.WriteString("kind", Misfire(definition.Misfire.Kind));
        writer.WriteNumber("catchUpLimit", definition.Misfire.CatchUpLimit);
        writer.WriteEndObject();
        writer.WriteString("overlap", Overlap(definition.Overlap));
        writer.WriteBoolean("enabled", definition.Enabled);
        writer.WriteEndObject();
    }

    private static void WriteState(Utf8JsonWriter writer, ScheduleState state)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", state.SchemaVersion);
        writer.WriteString("scheduleId", state.ScheduleId.Value);
        writer.WriteNumber("definitionRevision", state.DefinitionRevision);
        writer.WriteString("definitionHash", state.DefinitionHash);
        writer.WriteNumber("stateRevision", state.StateRevision);
        writer.WriteBoolean("enabled", state.Enabled);
        WriteOccurrence(writer, "nextOccurrence", state.NextOccurrence);
        WriteCatchUpEpisode(writer, "catchUpEpisode", state.CatchUpEpisode);
        WriteDeferredOccurrence(writer, "deferredOccurrence", state.DeferredOccurrence);
        WriteNullableUtc(writer, "lastClockObservedAtUtc", state.LastClockObservedAtUtc);
        WritePending(writer, state.PendingDelivery);
        writer.WriteStartArray("dispositionEvidence");
        foreach (var evidence in state.DispositionEvidence)
        {
            WriteDisposition(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("terminalDeliveryEvidence");
        foreach (var evidence in state.TerminalDeliveryEvidence)
        {
            WriteTerminalDeliveryEvidence(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePending(Utf8JsonWriter writer, SchedulePendingDelivery? pending)
    {
        if (pending is null)
        {
            writer.WriteNull("pendingDelivery");
            return;
        }

        writer.WriteStartObject("pendingDelivery");
        writer.WriteNumber("schemaVersion", pending.SchemaVersion);
        writer.WriteString("phase", PendingPhase(pending.Phase));
        WriteOccurrence(writer, "occurrence", pending.Occurrence);
        writer.WriteStartObject("identity");
        writer.WriteString("occurrenceId", pending.Identity.OccurrenceId.Value);
        writer.WriteString("deliveryId", pending.Identity.DeliveryId.Value);
        writer.WriteString("deduplicationId", pending.Identity.DeduplicationId.Value);
        writer.WriteEndObject();
        writer.WriteString("claimId", pending.ClaimId.Value);
        writer.WriteString("claimedAtUtc", ScheduleIdentityDerivation.Utc(pending.ClaimedAtUtc));
        writer.WriteString("currentEvidenceHash", pending.CurrentEvidenceHash);
        writer.WriteString("recurrenceProofHash", pending.RecurrenceProofHash);
        writer.WriteString("overlapEvidenceHash", pending.OverlapEvidenceHash);
        WriteFinalizationPlan(writer, pending.FinalizationPlan);
        WritePrepared(writer, pending.Prepared);
        WriteResult(writer, pending.Result);
        writer.WriteEndObject();
    }

    private static void WriteFinalizationPlan(Utf8JsonWriter writer, ScheduleFinalizationPlan? plan)
    {
        if (plan is null)
        {
            writer.WriteNull("finalizationPlan");
            return;
        }

        writer.WriteStartObject("finalizationPlan");
        writer.WriteNumber("schemaVersion", plan.SchemaVersion);
        WriteOccurrence(writer, "nextOccurrence", plan.NextOccurrence);
        WriteCatchUpEpisode(writer, "catchUpEpisode", plan.CatchUpEpisode);
        WriteDeferredOccurrence(writer, "deferredOccurrence", plan.DeferredOccurrence);
        writer.WriteStartArray("dispositionEvidence");
        foreach (var evidence in plan.DispositionEvidence)
        {
            WriteDisposition(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePrepared(Utf8JsonWriter writer, SchedulePreparedDelivery? prepared)
    {
        if (prepared is null)
        {
            writer.WriteNull("prepared");
            return;
        }

        if (!TriggerDeliveryJson.TrySerialize(prepared.Envelope, out var canonicalEnvelope, out _))
        {
            throw new InvalidOperationException("A validated prepared delivery must have canonical envelope JSON.");
        }

        writer.WriteStartObject("prepared");
        writer.WriteNumber("schemaVersion", prepared.SchemaVersion);
        writer.WriteString("canonicalEnvelope", canonicalEnvelope);
        writer.WriteString("canonicalEnvelopeHash", prepared.CanonicalEnvelopeHash);
        writer.WriteString("preparedAtUtc", ScheduleIdentityDerivation.Utc(prepared.PreparedAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteResult(Utf8JsonWriter writer, ScheduleDeliveryResultEvidence? result)
    {
        if (result is null)
        {
            writer.WriteNull("result");
            return;
        }

        writer.WriteStartObject("result");
        WriteResultValue(writer, result);
        writer.WriteEndObject();
    }

    private static void WriteResultValue(Utf8JsonWriter writer, ScheduleDeliveryResultEvidence result)
    {
        writer.WriteNumber("schemaVersion", result.SchemaVersion);
        writer.WriteString("kind", DeliveryResult(result.Kind));
        writer.WriteString("reasonCode", result.ReasonCode);
        writer.WriteString("canonicalEnvelopeHash", result.CanonicalEnvelopeHash);
        writer.WriteString("recordedAtUtc", ScheduleIdentityDerivation.Utc(result.RecordedAtUtc));
    }

    private static void WriteTerminalDeliveryEvidence(Utf8JsonWriter writer, ScheduleTerminalDeliveryEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        WriteOccurrence(writer, "occurrence", evidence.Occurrence);
        writer.WriteStartObject("identity");
        writer.WriteString("occurrenceId", evidence.Identity.OccurrenceId.Value);
        writer.WriteString("deliveryId", evidence.Identity.DeliveryId.Value);
        writer.WriteString("deduplicationId", evidence.Identity.DeduplicationId.Value);
        writer.WriteEndObject();
        writer.WriteString("currentEvidenceHash", evidence.CurrentEvidenceHash);
        writer.WriteString("recurrenceProofHash", evidence.RecurrenceProofHash);
        writer.WriteString("overlapEvidenceHash", evidence.OverlapEvidenceHash);
        writer.WriteStartObject("result");
        WriteResultValue(writer, evidence.Result);
        writer.WriteEndObject();
        writer.WriteString("finalizedAtUtc", ScheduleIdentityDerivation.Utc(evidence.FinalizedAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteCatchUpEpisode(Utf8JsonWriter writer, string propertyName, ScheduleCatchUpEpisode? episode)
    {
        if (episode is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteNumber("schemaVersion", episode.SchemaVersion);
        writer.WriteNumber("latestDueOrdinal", episode.LatestDueOrdinal);
        writer.WriteNumber("remainingAdmittedOccurrences", episode.RemainingAdmittedOccurrences);
        writer.WriteEndObject();
    }

    private static void WriteDeferredOccurrence(Utf8JsonWriter writer, string propertyName, ScheduleDeferredOccurrence? deferred)
    {
        if (deferred is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteNumber("schemaVersion", deferred.SchemaVersion);
        WriteOccurrence(writer, "occurrence", deferred.Occurrence);
        writer.WriteStartObject("identity");
        writer.WriteString("occurrenceId", deferred.Identity.OccurrenceId.Value);
        writer.WriteString("deliveryId", deferred.Identity.DeliveryId.Value);
        writer.WriteString("deduplicationId", deferred.Identity.DeduplicationId.Value);
        writer.WriteEndObject();
        writer.WriteString("deferredAtUtc", ScheduleIdentityDerivation.Utc(deferred.DeferredAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteOccurrence(Utf8JsonWriter writer, string propertyName, ScheduleOccurrence? occurrence)
    {
        if (occurrence is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteNumber("schemaVersion", occurrence.SchemaVersion);
        writer.WriteNumber("ordinal", occurrence.Ordinal);
        writer.WriteString("scheduledLocal", ScheduleIdentityDerivation.Local(occurrence.ScheduledLocal));
        writer.WriteString("scheduledAtUtc", ScheduleIdentityDerivation.Utc(occurrence.ScheduledAtUtc));
        WriteTimeZone(writer, "timeZone", occurrence.TimeZone);
        writer.WriteEndObject();
    }

    private static void WriteDisposition(Utf8JsonWriter writer, ScheduleOccurrenceDispositionEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteNumber("firstOrdinal", evidence.FirstOrdinal);
        writer.WriteNumber("lastOrdinal", evidence.LastOrdinal);
        writer.WriteNumber("count", evidence.Count);
        writer.WriteString("firstScheduledLocal", ScheduleIdentityDerivation.Local(evidence.FirstScheduledLocal));
        writer.WriteString("lastScheduledLocal", ScheduleIdentityDerivation.Local(evidence.LastScheduledLocal));
        WriteNullableUtc(writer, "firstScheduledAtUtc", evidence.FirstScheduledAtUtc);
        WriteNullableUtc(writer, "lastScheduledAtUtc", evidence.LastScheduledAtUtc);
        WriteTimeZone(writer, "timeZone", evidence.TimeZone);
        writer.WriteString("disposition", Disposition(evidence.Disposition));
        writer.WriteString("decisionEvidenceHash", evidence.DecisionEvidenceHash);
        writer.WriteString("reasonCode", evidence.ReasonCode);
        writer.WriteString("recordedAtUtc", ScheduleIdentityDerivation.Utc(evidence.RecordedAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteTimeZone(Utf8JsonWriter writer, string propertyName, ScheduleTimeZoneReference timeZone)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("timeZoneId", timeZone.TimeZoneId);
        writer.WriteString("rulesFingerprint", timeZone.RulesFingerprint);
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, long? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }

    private static void WriteNullableUtc(Utf8JsonWriter writer, string propertyName, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, ScheduleIdentityDerivation.Utc(value.Value));
        }
    }

    private static string Priority(SchedulePriority value) => value switch
    {
        SchedulePriority.Background => "background",
        SchedulePriority.Normal => "normal",
        SchedulePriority.Elevated => "elevated",
        SchedulePriority.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Recurrence(ScheduleRecurrenceKind value) => value switch
    {
        ScheduleRecurrenceKind.Once => "once",
        ScheduleRecurrenceKind.FixedInterval => "fixed-interval",
        ScheduleRecurrenceKind.Daily => "daily",
        ScheduleRecurrenceKind.Weekly => "weekly",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string InvalidLocalTime(ScheduleInvalidLocalTimePolicy value) => value switch
    {
        ScheduleInvalidLocalTimePolicy.Skip => "skip",
        ScheduleInvalidLocalTimePolicy.ShiftForward => "shift-forward",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string AmbiguousLocalTime(ScheduleAmbiguousLocalTimePolicy value) => value switch
    {
        ScheduleAmbiguousLocalTimePolicy.EarlierUtc => "earlier-utc",
        ScheduleAmbiguousLocalTimePolicy.LaterUtc => "later-utc",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Misfire(ScheduleMisfirePolicyKind value) => value switch
    {
        ScheduleMisfirePolicyKind.Skip => "skip",
        ScheduleMisfirePolicyKind.FireLatestOnce => "fire-latest-once",
        ScheduleMisfirePolicyKind.CatchUp => "catch-up",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Overlap(ScheduleOverlapPolicy value) => value switch
    {
        ScheduleOverlapPolicy.Allow => "allow",
        ScheduleOverlapPolicy.Skip => "skip",
        ScheduleOverlapPolicy.DeferOne => "defer-one",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string DeliveryResult(ScheduleDeliveryResultKind value) => value switch
    {
        ScheduleDeliveryResultKind.Queued => "queued",
        ScheduleDeliveryResultKind.Replayed => "replayed",
        ScheduleDeliveryResultKind.Rejected => "rejected",
        ScheduleDeliveryResultKind.Backpressured => "backpressured",
        ScheduleDeliveryResultKind.Unavailable => "unavailable",
        ScheduleDeliveryResultKind.Ambiguous => "ambiguous",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string PendingPhase(SchedulePendingDeliveryPhase value) => value switch
    {
        SchedulePendingDeliveryPhase.Claimed => "claimed",
        SchedulePendingDeliveryPhase.Prepared => "prepared",
        SchedulePendingDeliveryPhase.ResultObserved => "result-observed",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Disposition(ScheduleOccurrenceDisposition value) => value switch
    {
        ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped => "invalid-local-time-skipped",
        ScheduleOccurrenceDisposition.MisfireSkipped => "misfire-skipped",
        ScheduleOccurrenceDisposition.OverlapSkipped => "overlap-skipped",
        ScheduleOccurrenceDisposition.OverlapDeferred => "overlap-deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
