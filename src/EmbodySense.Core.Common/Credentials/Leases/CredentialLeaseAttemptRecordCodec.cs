using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Common.Credentials.Leases;

/// <summary>Encodes strict canonical schema-1 credential lease histories.</summary>
public static class CredentialLeaseAttemptRecordCodec
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = false,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Serializes one validated immutable history to compact canonical UTF-8 JSON.</summary>
    public static byte[] Encode(CredentialLeaseAttemptHistory history)
    {
        var reason = CredentialLeaseContract.Validate(history);
        if (reason is not null)
        {
            throw new ArgumentException(reason, nameof(history));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(history, _options);
        return bytes.Length <= CredentialLeaseContractLimits.MaximumRecordUtf8Bytes
            ? bytes
            : throw new ArgumentException("credential-lease-history-too-large", nameof(history));
    }

    /// <summary>Strictly decodes and validates canonical UTF-8 JSON without accepting aliases or unknown members.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> utf8Json, out CredentialLeaseAttemptHistory? history, out string? reasonCode)
    {
        history = null;
        reasonCode = "credential-lease-history-malformed";
        if (utf8Json.IsEmpty || utf8Json.Length > CredentialLeaseContractLimits.MaximumRecordUtf8Bytes)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CredentialLeaseAttemptHistory>(utf8Json, _options);
            if (parsed is null || !utf8Json.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed, _options)))
            {
                return false;
            }

            var validation = CredentialLeaseContract.Validate(parsed);
            if (validation is not null)
            {
                reasonCode = validation;
                return false;
            }

            history = CredentialLeaseContract.CreateHistory(parsed.Intent with { }, parsed.Versions.Select(version => version with { }));
            reasonCode = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            history = null;
            return false;
        }
    }
}
