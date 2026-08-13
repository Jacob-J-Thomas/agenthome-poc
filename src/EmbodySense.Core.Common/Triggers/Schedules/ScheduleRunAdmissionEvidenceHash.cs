using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Applies and verifies the canonical digest of schedule run-admission evidence.</summary>
public static class ScheduleRunAdmissionEvidenceHash
{
    /// <summary>Returns a copy carrying the digest of all exact evidence fields.</summary>
    public static ScheduleRunAdmissionEvidence Apply(ScheduleRunAdmissionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with { ContentHash = Compute(evidence) };
    }

    /// <summary>Determines whether the retained digest matches all exact evidence fields.</summary>
    public static bool Matches(ScheduleRunAdmissionEvidence? evidence)
        => evidence is not null
            && IsHash(evidence.ContentHash)
            && string.Equals(evidence.ContentHash, Compute(evidence), StringComparison.Ordinal);

    /// <summary>Computes the lowercase SHA-256 digest without trusting the retained digest.</summary>
    public static string Compute(ScheduleRunAdmissionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteString("canonicalEnvelope", evidence.CanonicalEnvelope);
        writer.WriteString("canonicalEnvelopeHash", evidence.CanonicalEnvelopeHash);
        writer.WriteString("loopId", evidence.LoopId);
        writer.WritePropertyName("attempts");
        writer.WriteStartArray();
        foreach (var attempt in evidence.Attempts ?? [])
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", attempt.SchemaVersion);
            writer.WriteNumber("ordinal", attempt.Ordinal);
            writer.WriteString("disposition", Disposition(attempt.Disposition));
            writer.WriteString("admissionOperationId", attempt.AdmissionOperationId);
            writer.WriteString("candidateRunId", attempt.CandidateRunId);
            if (attempt.BlockingRunId is null)
            {
                writer.WriteNull("blockingRunId");
            }
            else
            {
                writer.WriteString("blockingRunId", attempt.BlockingRunId);
            }

            writer.WriteString("recordedAtUtc", attempt.RecordedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static string Disposition(ScheduleRunAdmissionDisposition value)
        => value switch
        {
            ScheduleRunAdmissionDisposition.RunCreated => "run-created",
            ScheduleRunAdmissionDisposition.OverlapSkipped => "overlap-skipped",
            ScheduleRunAdmissionDisposition.OverlapDeferred => "overlap-deferred",
            ScheduleRunAdmissionDisposition.OverlapSerialized => "overlap-serialized",
            ScheduleRunAdmissionDisposition.DeferredOneSuppressed => "deferred-one-suppressed",
            _ => "unknown",
        };

    private static bool IsHash(string? value)
        => value?.Length == TriggerDeliveryLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
