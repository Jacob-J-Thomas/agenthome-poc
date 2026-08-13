using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Derives stable occurrence, delivery, and deduplication identities from immutable schedule coordinates.</summary>
public static class ScheduleIdentityDerivation
{
    private const string DeliveryPrefix = "schedule-delivery-";
    private const string DeduplicationPrefix = "schedule-deduplication-";

    /// <summary>Derives domain-separated identities from the exact definition version/hash and selected occurrence.</summary>
    public static bool TryDerive(
        ScheduleId? scheduleId,
        long definitionRevision,
        string? definitionHash,
        ScheduleOccurrence? occurrence,
        out ScheduleOccurrenceIdentity? identity,
        out ScheduleContractValidationResult validation)
    {
        validation = ScheduleContractValidator.ValidateIdentityCoordinates(scheduleId, definitionRevision, definitionHash, occurrence);
        if (!validation.IsValid)
        {
            identity = null;
            return false;
        }

        var seed = CanonicalSeed(scheduleId!, definitionRevision, definitionHash!, occurrence!);
        var occurrenceHash = DomainHash("embodysense.schedule-occurrence.v1", seed);
        var deliveryHash = DomainHash("embodysense.schedule-delivery.v1", seed);
        var deduplicationHash = DomainHash("embodysense.schedule-deduplication.v1", seed);
        if (!TriggerDeliveryId.TryParse(DeliveryPrefix + deliveryHash, out var deliveryId)
            || !TriggerDeduplicationId.TryParse(DeduplicationPrefix + deduplicationHash, out var deduplicationId))
        {
            identity = null;
            validation = new ScheduleContractValidationResult([new ScheduleContractError("identity_derivation_failed", "$")]);
            return false;
        }

        identity = new ScheduleOccurrenceIdentity(
            ScheduleOccurrenceId.Create(occurrenceHash),
            deliveryId!,
            deduplicationId!);
        return true;
    }

    /// <summary>Determines whether supplied identities exactly match their immutable derivation coordinates.</summary>
    public static bool Matches(
        ScheduleOccurrenceIdentity? identity,
        ScheduleId? scheduleId,
        long definitionRevision,
        string? definitionHash,
        ScheduleOccurrence? occurrence)
        => identity is not null
            && TryDerive(scheduleId, definitionRevision, definitionHash, occurrence, out var expected, out _)
            && Equals(identity, expected);

    private static byte[] CanonicalSeed(
        ScheduleId scheduleId,
        long definitionRevision,
        string definitionHash,
        ScheduleOccurrence occurrence)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", ScheduleContractLimits.CurrentSchemaVersion);
        writer.WriteString("scheduleId", scheduleId.Value);
        writer.WriteNumber("definitionRevision", definitionRevision);
        writer.WriteString("definitionHash", definitionHash);
        writer.WriteNumber("occurrenceOrdinal", occurrence.Ordinal);
        writer.WriteString("scheduledLocal", Local(occurrence.ScheduledLocal));
        writer.WriteString("scheduledAtUtc", Utc(occurrence.ScheduledAtUtc));
        writer.WriteString("timeZoneId", occurrence.TimeZone.TimeZoneId);
        writer.WriteString("timeZoneRulesFingerprint", occurrence.TimeZone.RulesFingerprint);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static string DomainHash(string domain, byte[] seed)
    {
        var domainBytes = Encoding.ASCII.GetBytes(domain + "\n");
        var content = new byte[domainBytes.Length + seed.Length];
        domainBytes.CopyTo(content, 0);
        seed.CopyTo(content, domainBytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    internal static string Local(DateTime value)
        => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    internal static string Utc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
