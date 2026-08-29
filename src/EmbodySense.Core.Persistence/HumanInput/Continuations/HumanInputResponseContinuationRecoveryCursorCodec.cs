using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Persistence.HumanInput.Continuations.Models;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Persistence.HumanInput.Continuations;

/// <summary>Encodes strict canonical cursors for bounded Human Input response-continuation discovery.</summary>
internal static class HumanInputResponseContinuationRecoveryCursorCodec
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    internal static HumanInputResponseContinuationRecoveryCursor First()
        => new(CurrentSchemaVersion, null, null, null, 0);

    internal static string Encode(HumanInputResponseContinuationRecoveryCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Validate(cursor);
        var payload = new HumanInputResponseContinuationRecoveryCursorPayload(
            cursor.SchemaVersion,
            cursor.AfterRunCursor,
            cursor.ResumeRunId,
            cursor.ResumeRunCreatedAtUtcTicks,
            cursor.NextCheckpointOrdinal);
        var encoded = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
        if (encoded.Length > CustomLoopLimits.MaxRunPageCursorCharacters)
        {
            throw InvalidCursor();
        }
        return encoded;
    }

    internal static HumanInputResponseContinuationRecoveryCursor Decode(string? encoded)
    {
        if (encoded is null)
        {
            return First();
        }
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > CustomLoopLimits.MaxRunPageCursorCharacters)
        {
            throw InvalidCursor();
        }

        try
        {
            var payload = JsonSerializer.Deserialize<HumanInputResponseContinuationRecoveryCursorPayload>(Base64UrlDecode(encoded), _jsonOptions);
            var cursor = payload is null
                ? null
                : new HumanInputResponseContinuationRecoveryCursor(
                    payload.SchemaVersion,
                    payload.AfterRunCursor,
                    payload.ResumeRunId,
                    payload.ResumeRunCreatedAtUtcTicks,
                    payload.NextCheckpointOrdinal);
            if (cursor is null)
            {
                throw InvalidCursor();
            }

            Validate(cursor);
            if (!string.Equals(Encode(cursor), encoded, StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }

            return cursor;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            throw InvalidCursor(exception);
        }
    }

    private static void Validate(HumanInputResponseContinuationRecoveryCursor cursor)
    {
        if (cursor.SchemaVersion != CurrentSchemaVersion
            || cursor.AfterRunCursor is { Length: > CustomLoopLimits.MaxRunPageCursorCharacters }
            || cursor.AfterRunCursor is not null && string.IsNullOrWhiteSpace(cursor.AfterRunCursor)
            || cursor.NextCheckpointOrdinal is < 0 or > GovernedLoopExecutionLimits.MaxFrontierNodes
            || (cursor.ResumeRunId is null) != (cursor.ResumeRunCreatedAtUtcTicks is null)
            || cursor.ResumeRunId is null && cursor.NextCheckpointOrdinal != 0
            || cursor.ResumeRunId is not null && !CustomLoopArtifactIdentifier.IsValid(cursor.ResumeRunId)
            || cursor.ResumeRunCreatedAtUtcTicks is { } ticks
                && new DateTimeOffset(ticks, TimeSpan.Zero) < DateTimeOffset.UnixEpoch)
        {
            throw InvalidCursor();
        }

        if (cursor.AfterRunCursor is not null)
        {
            _ = CustomLoopRunPageCursorCodec.Decode(cursor.AfterRunCursor, null)
                ?? throw InvalidCursor();
        }
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 == 1)
        {
            throw new FormatException("The cursor is not canonical base64url.");
        }

        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    private static ArgumentException InvalidCursor(Exception? innerException = null)
        => new("The Human Input response-continuation recovery cursor is invalid.", "scanCursor", innerException);
}
