using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationJournalCodec
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static byte[] Encode(GovernedLoopEffectReconciliationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var unsigned = journal with { ContentHash = string.Empty };
        var hash = GovernedLoopEffectReconciliationPersistenceHash.Compute("journal", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(unsigned, _json)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal with { ContentHash = hash }, _json);
        if (bytes.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumJournalUtf8Bytes)
        {
            throw new FormatException("The reconciliation transaction journal exceeds its bounded size.");
        }

        return bytes;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out GovernedLoopEffectReconciliationJournal? journal)
    {
        journal = null;
        if (bytes.IsEmpty || bytes.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumJournalUtf8Bytes)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<GovernedLoopEffectReconciliationJournal>(bytes, _json);
            if (parsed is null)
            {
                return false;
            }

            var unsigned = parsed with { ContentHash = string.Empty };
            var expected = GovernedLoopEffectReconciliationPersistenceHash.Compute("journal", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(unsigned, _json)));
            if (!string.Equals(expected, parsed.ContentHash, StringComparison.Ordinal)
                || !bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed, _json)))
            {
                return false;
            }

            journal = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
