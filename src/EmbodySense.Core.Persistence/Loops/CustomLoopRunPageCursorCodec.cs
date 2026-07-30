using EmbodySense.Core.Common.Loops.Custom;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Encodes and validates opaque version-1 continuation cursors for deterministic run-summary pagination.
/// </summary>
internal static class CustomLoopRunPageCursorCodec
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    /// <summary>
    /// Encodes a cursor as canonical unpadded base64url JSON.
    /// </summary>
    /// <param name="cursor">The cursor.</param>
    /// <returns>The opaque canonical cursor.</returns>
    public static string Encode(CustomLoopRunPageCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var payload = new CursorPayload(CurrentVersion, cursor.CreatedAtUtc.UtcTicks, cursor.RunId, cursor.LoopId);
        return Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
    }

    /// <summary>
    /// Decodes a canonical cursor and verifies that it belongs to the active loop filter.
    /// </summary>
    /// <param name="encoded">The opaque cursor, or <see langword="null"/> for the first page.</param>
    /// <param name="expectedLoopId">The exact loop filter used by the page request, or <see langword="null"/> for an unfiltered page.</param>
    /// <returns>The decoded cursor, or <see langword="null"/> when <paramref name="encoded"/> is <see langword="null"/>.</returns>
    public static CustomLoopRunPageCursor? Decode(string? encoded, string? expectedLoopId)
    {
        if (encoded is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > CustomLoopLimits.MaxRunPageCursorCharacters)
        {
            throw InvalidCursor();
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(Base64UrlDecode(encoded), _jsonOptions);
            if (payload is null
                || payload.Version != CurrentVersion
                || !CustomLoopArtifactIdentifier.IsValid(payload.RunId)
                || payload.LoopId is not null && !CustomLoopArtifactIdentifier.IsValid(payload.LoopId)
                || !string.Equals(payload.LoopId, expectedLoopId, StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }

            var cursor = new CustomLoopRunPageCursor(
                new DateTimeOffset(payload.CreatedAtUtcTicks, TimeSpan.Zero),
                payload.RunId,
                payload.LoopId);
            if (cursor.CreatedAtUtc < DateTimeOffset.UnixEpoch || !string.Equals(Encode(cursor), encoded, StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }

            return cursor;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentOutOfRangeException)
        {
            throw InvalidCursor(exception);
        }
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var remainder = base64.Length % 4;
        if (remainder == 1)
        {
            throw new FormatException("The cursor is not canonical base64url.");
        }

        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - remainder) % 4, '='));
    }

    private static ArgumentException InvalidCursor(Exception? innerException = null) => new("The custom-loop run cursor is invalid or belongs to a different loop filter.", "cursor", innerException);

    private sealed record CursorPayload(int Version, long CreatedAtUtcTicks, string RunId, string? LoopId);
}
