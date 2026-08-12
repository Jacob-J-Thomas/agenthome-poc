using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>Hashes the exact durable custom-run event payload named by sequential node evidence.</summary>
public static class CustomLoopSequentialOutcomeArtifactHash
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Computes the event digest after clearing the self-referential sequential-evidence field.</summary>
    public static string Compute(CustomLoopRunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(runEvent with { SequentialNodeEvidence = null }, _jsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    /// <summary>Returns whether the evidence names the exact containing durable event payload.</summary>
    public static bool Matches(CustomLoopRunEvent? runEvent)
    {
        if (runEvent?.SequentialNodeEvidence?.OutcomeArtifactHash is not { Length: 64 } actual
            || actual.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            return false;
        }

        var expected = Compute(runEvent);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(actual));
    }
}
