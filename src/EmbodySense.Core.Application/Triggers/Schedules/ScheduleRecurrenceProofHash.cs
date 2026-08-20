using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Computes the exact proof hash for one immutable successor plan.</summary>
public static class ScheduleRecurrenceProofHash
{
    /// <summary>Hashes the pinned definition, current occurrence, and complete successor plan.</summary>
    public static string Compute(
        string definitionHash,
        ScheduleOccurrence occurrence,
        ScheduleFinalizationPlan plan,
        IReadOnlyList<string> resolutionEvidenceHashes)
    {
        ArgumentException.ThrowIfNullOrEmpty(definitionHash);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(resolutionEvidenceHashes);
        if (!IsSha256(definitionHash)
            || !ScheduleContractValidator.ValidateOccurrence(occurrence).IsValid
            || !ScheduleContractValidator.ValidateFinalizationPlan(plan).IsValid
            || resolutionEvidenceHashes.Count > ScheduleContractLimits.MaxFinalizationEvidenceItems + 1
            || resolutionEvidenceHashes.Any(hash => !IsSha256(hash)))
        {
            throw new ArgumentException("Recurrence proof input is outside the bounded schedule contract.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("definitionHash", definitionHash);
            WriteOccurrence(writer, "currentOccurrence", occurrence);
            WriteOccurrence(writer, "nextOccurrence", plan.NextOccurrence);
            writer.WriteStartArray("resolutionEvidenceHashes");
            foreach (var hash in resolutionEvidenceHashes)
            {
                writer.WriteStringValue(hash);
            }

            writer.WriteEndArray();
            if (plan.CatchUpEpisode is null)
            {
                writer.WriteNull("catchUpEpisode");
            }
            else
            {
                writer.WriteStartObject("catchUpEpisode");
                writer.WriteNumber("latestDueOrdinal", plan.CatchUpEpisode.LatestDueOrdinal);
                writer.WriteNumber("remainingAdmittedOccurrences", plan.CatchUpEpisode.RemainingAdmittedOccurrences);
                writer.WriteEndObject();
            }

            if (plan.DeferredOccurrence is null)
            {
                writer.WriteNull("deferredOccurrence");
            }
            else
            {
                writer.WriteStartObject("deferredOccurrence");
                WriteOccurrence(writer, "occurrence", plan.DeferredOccurrence.Occurrence);
                writer.WriteString("occurrenceId", plan.DeferredOccurrence.Identity.OccurrenceId.Value);
                writer.WriteString("deliveryId", plan.DeferredOccurrence.Identity.DeliveryId.Value);
                writer.WriteString("deduplicationId", plan.DeferredOccurrence.Identity.DeduplicationId.Value);
                writer.WriteString("deferredAtUtc", plan.DeferredOccurrence.DeferredAtUtc);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("dispositionEvidence");
            foreach (var evidence in plan.DispositionEvidence)
            {
                writer.WriteStartObject();
                writer.WriteNumber("firstOrdinal", evidence.FirstOrdinal);
                writer.WriteNumber("lastOrdinal", evidence.LastOrdinal);
                writer.WriteNumber("count", evidence.Count);
                writer.WriteString("firstScheduledLocal", evidence.FirstScheduledLocal);
                writer.WriteString("lastScheduledLocal", evidence.LastScheduledLocal);
                WriteNullableUtc(writer, "firstScheduledAtUtc", evidence.FirstScheduledAtUtc);
                WriteNullableUtc(writer, "lastScheduledAtUtc", evidence.LastScheduledAtUtc);
                writer.WriteString("timeZoneId", evidence.TimeZone.TimeZoneId);
                writer.WriteString("rulesFingerprint", evidence.TimeZone.RulesFingerprint);
                writer.WriteNumber("disposition", (int)evidence.Disposition);
                writer.WriteString("decisionEvidenceHash", evidence.DecisionEvidenceHash);
                writer.WriteString("reasonCode", evidence.ReasonCode);
                writer.WriteString("recordedAtUtc", evidence.RecordedAtUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>Hashes one exact local-time rules response, including gap/fold proof.</summary>
    public static string ComputeLocalResolution(
        ScheduleTimeZoneReference timeZone,
        DateTime requestedLocal,
        ScheduleTimeZoneResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ValidateResolutionInput(timeZone, resolution.RulesFingerprint);
        return Hash(writer =>
        {
            writer.WriteString("kind", "local");
            writer.WriteString("timeZoneId", timeZone.TimeZoneId);
            writer.WriteString("pinnedRulesFingerprint", timeZone.RulesFingerprint);
            writer.WriteString("requestedLocal", requestedLocal);
            writer.WriteNumber("status", (int)resolution.Status);
            writer.WriteString("returnedRulesFingerprint", resolution.RulesFingerprint);
            writer.WriteString("resolvedLocal", resolution.ResolvedLocal);
            WriteNullableUtc(writer, "earlierUtc", resolution.EarlierUtc);
            WriteNullableUtc(writer, "laterUtc", resolution.LaterUtc);
        });
    }

    /// <summary>Hashes one exact UTC-to-local rules response used by fixed recurrence.</summary>
    public static string ComputeInstantResolution(
        ScheduleTimeZoneReference timeZone,
        DateTimeOffset requestedUtc,
        ScheduleInstantResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ValidateResolutionInput(timeZone, resolution.RulesFingerprint);
        return Hash(writer =>
        {
            writer.WriteString("kind", "instant");
            writer.WriteString("timeZoneId", timeZone.TimeZoneId);
            writer.WriteString("pinnedRulesFingerprint", timeZone.RulesFingerprint);
            writer.WriteString("requestedUtc", requestedUtc);
            writer.WriteNumber("status", (int)resolution.Status);
            writer.WriteString("returnedRulesFingerprint", resolution.RulesFingerprint);
            writer.WriteString("scheduledLocal", resolution.ScheduledLocal);
        });
    }

    private static void ValidateResolutionInput(ScheduleTimeZoneReference? timeZone, string? returnedFingerprint)
    {
        if (timeZone?.TimeZoneId is null
            || timeZone.TimeZoneId.Length is < 1 or > ScheduleContractLimits.MaxTimeZoneIdCharacters
            || !IsSha256(timeZone.RulesFingerprint)
            || returnedFingerprint is not null && !IsSha256(returnedFingerprint))
        {
            throw new ArgumentException("Time-zone proof input is outside the bounded schedule contract.");
        }
    }

    private static bool IsSha256(string? value)
        => value?.Length == ScheduleContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            write(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteOccurrence(Utf8JsonWriter writer, string propertyName, ScheduleOccurrence? occurrence)
    {
        if (occurrence is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteNumber("ordinal", occurrence.Ordinal);
        writer.WriteString("scheduledLocal", occurrence.ScheduledLocal);
        writer.WriteString("scheduledAtUtc", occurrence.ScheduledAtUtc);
        writer.WriteString("timeZoneId", occurrence.TimeZone.TimeZoneId);
        writer.WriteString("rulesFingerprint", occurrence.TimeZone.RulesFingerprint);
        writer.WriteEndObject();
    }

    private static void WriteNullableUtc(Utf8JsonWriter writer, string propertyName, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value.Value);
        }
    }
}
