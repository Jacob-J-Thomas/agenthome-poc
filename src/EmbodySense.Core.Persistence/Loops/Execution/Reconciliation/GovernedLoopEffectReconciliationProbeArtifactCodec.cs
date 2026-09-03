using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationProbeArtifactCodec
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static byte[] EncodeReservation(GovernedLoopEffectReconciliationProbeReservationArtifact value)
        => Encode(value with { ContentHash = string.Empty }, "probe-reservation", GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservationUtf8Bytes);

    internal static byte[] EncodeObservation(GovernedLoopEffectReconciliationProbeObservationArtifact value)
        => Encode(value with { ContentHash = string.Empty }, "probe-observation", GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeObservationUtf8Bytes);

    internal static byte[] EncodeJournal(GovernedLoopEffectReconciliationProbeJournal value)
        => Encode(value with { ContentHash = string.Empty }, "probe-journal", GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeJournalUtf8Bytes);

    internal static bool TryDecodeReservation(ReadOnlySpan<byte> bytes, out GovernedLoopEffectReconciliationProbeReservationArtifact? value)
        => TryDecode(bytes, "probe-reservation", GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservationUtf8Bytes, out value);

    internal static bool TryDecodeObservation(ReadOnlySpan<byte> bytes, out GovernedLoopEffectReconciliationProbeObservationArtifact? value)
        => TryDecode(bytes, "probe-observation", GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeObservationUtf8Bytes, out value);

    internal static bool TryDecodeJournal(ReadOnlySpan<byte> bytes, out GovernedLoopEffectReconciliationProbeJournal? value)
        => TryDecode(bytes, "probe-journal", GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeJournalUtf8Bytes, out value);

    private static byte[] Encode<T>(T unsignedValue, string domain, int maximumBytes)
    {
        var unsigned = JsonSerializer.SerializeToUtf8Bytes(unsignedValue, _json);
        var hash = GovernedLoopEffectReconciliationPersistenceHash.Compute(domain, Convert.ToBase64String(unsigned));
        var value = typeof(T) == typeof(GovernedLoopEffectReconciliationProbeReservationArtifact)
            ? (T)(object)((GovernedLoopEffectReconciliationProbeReservationArtifact)(object)unsignedValue! with { ContentHash = hash })
            : typeof(T) == typeof(GovernedLoopEffectReconciliationProbeObservationArtifact)
                ? (T)(object)((GovernedLoopEffectReconciliationProbeObservationArtifact)(object)unsignedValue! with { ContentHash = hash })
                : (T)(object)((GovernedLoopEffectReconciliationProbeJournal)(object)unsignedValue! with { ContentHash = hash });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _json);
        if (bytes.Length > maximumBytes)
        {
            throw new FormatException($"The {domain} artifact exceeds its bounded size.");
        }

        return bytes;
    }

    private static bool TryDecode<T>(ReadOnlySpan<byte> bytes, string domain, int maximumBytes, out T? value)
    {
        value = default;
        if (bytes.IsEmpty || bytes.Length > maximumBytes)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(bytes, _json);
            if (parsed is null)
            {
                return false;
            }

            var hash = parsed switch
            {
                GovernedLoopEffectReconciliationProbeReservationArtifact reservation => reservation.ContentHash,
                GovernedLoopEffectReconciliationProbeObservationArtifact observation => observation.ContentHash,
                GovernedLoopEffectReconciliationProbeJournal journal => journal.ContentHash,
                _ => null
            };
            var unsigned = parsed switch
            {
                GovernedLoopEffectReconciliationProbeReservationArtifact reservation => (T)(object)(reservation with { ContentHash = string.Empty }),
                GovernedLoopEffectReconciliationProbeObservationArtifact observation => (T)(object)(observation with { ContentHash = string.Empty }),
                GovernedLoopEffectReconciliationProbeJournal journal => (T)(object)(journal with { ContentHash = string.Empty }),
                _ => parsed
            };
            var expected = GovernedLoopEffectReconciliationPersistenceHash.Compute(domain, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(unsigned, _json)));
            if (!string.Equals(hash, expected, StringComparison.Ordinal) || !bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed, _json)))
            {
                return false;
            }

            value = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
