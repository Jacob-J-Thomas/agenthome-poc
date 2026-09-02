using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationReceiptCodec
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static byte[] Encode(GovernedLoopEffectReconciliationOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var canonical = receipt with { ContentHash = string.Empty };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, _json);
        var hashed = receipt with { ContentHash = GovernedLoopEffectReconciliationPersistenceHash.Compute("receipt", Convert.ToBase64String(bytes)) };
        bytes = JsonSerializer.SerializeToUtf8Bytes(hashed, _json);
        if (bytes.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumReceiptUtf8Bytes)
        {
            throw new FormatException("The reconciliation operation receipt exceeds its bounded size.");
        }

        return bytes;
    }

    internal static GovernedLoopEffectReconciliationOperationReceipt Materialize(GovernedLoopEffectReconciliationOperationReceipt receipt)
    {
        var bytes = Encode(receipt);
        return TryDecode(bytes, out var parsed) && parsed is not null
            ? parsed
            : throw new FormatException("The reconciliation operation receipt could not be canonicalized.");
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out GovernedLoopEffectReconciliationOperationReceipt? receipt)
    {
        receipt = null;
        if (bytes.IsEmpty || bytes.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumReceiptUtf8Bytes)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<GovernedLoopEffectReconciliationOperationReceipt>(bytes, _json);
            if (parsed is null)
            {
                return false;
            }

            var unsigned = parsed with { ContentHash = string.Empty };
            var expected = GovernedLoopEffectReconciliationPersistenceHash.Compute("receipt", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(unsigned, _json)));
            if (!string.Equals(expected, parsed.ContentHash, StringComparison.Ordinal)
                || !bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed, _json)))
            {
                return false;
            }

            receipt = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
