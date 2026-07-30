using System.Text.Json;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Centralizes persisted JSON nesting limits and normalizes depth failures to artifact <see cref="FormatException"/> values.
/// </summary>
internal static class CustomLoopJsonDepthPolicy
{
    // Operation receipts contain only shallow scalar fields and bounded validation-error arrays. This leaves shape-evolution headroom while bounding hostile or corrupt nesting.
    /// <summary>
    /// Identifies the maximum persisted nesting depth for shallow operation receipts.
    /// </summary>
    internal const int ShallowReceiptMaximumDepth = 32;

    // Canonical run artifacts contain bounded nested context, evidence, and projections and therefore use the larger persistence ceiling.
    /// <summary>
    /// Identifies the maximum persisted nesting depth for canonical run artifacts.
    /// </summary>
    internal const int CanonicalRunArtifactMaximumDepth = 64;

    /// <summary>
    /// Validates strict UTF-8 JSON syntax and rejects nesting at or beyond the artifact limit.
    /// </summary>
    /// <param name="utf8Json">The utf8 JSON.</param>
    /// <param name="maximumDepth">The maximum depth.</param>
    /// <param name="artifactName">The artifact name.</param>
    /// <param name="path">The path.</param>
    internal static void ValidatePersistedJsonDepth(ReadOnlySpan<byte> utf8Json, int maximumDepth, string artifactName, string? path = null)
    {
        var label = ArtifactLabel(artifactName, path);
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = maximumDepth + 1
        });

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= maximumDepth)
                {
                    throw DepthException(label, maximumDepth, innerException: null);
                }
            }
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{label} contains invalid JSON or UTF-8.", exception);
        }
    }

    /// <summary>
    /// Converts a serializer depth failure into the persistence layer's corruption exception.
    /// </summary>
    /// <param name="artifactName">The artifact name.</param>
    /// <param name="maximumDepth">The maximum depth.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <param name="path">The path.</param>
    /// <returns>The format exception.</returns>
    internal static FormatException SerializationDepthException(string artifactName, int maximumDepth, JsonException exception, string? path = null)
    {
        return DepthException(ArtifactLabel(artifactName, path), maximumDepth, exception);
    }

    /// <summary>
    /// Serializes a value to UTF-8 and normalizes JSON depth failures to <see cref="FormatException"/>.
    /// </summary>
    /// <typeparam name="T">The serialized value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">The options.</param>
    /// <param name="artifactName">The artifact name.</param>
    /// <param name="path">The path.</param>
    /// <returns>The serialized UTF-8 JSON bytes.</returns>
    internal static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions options, string artifactName, string? path = null)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, options);
        }
        catch (JsonException exception)
        {
            throw SerializationDepthException(artifactName, options.MaxDepth, exception, path);
        }
    }

    private static FormatException DepthException(string label, int maximumDepth, JsonException? innerException)
    {
        var message = $"{label} exceeds the maximum persisted JSON nesting depth of {maximumDepth}. This is an artifact-nesting safety limit, not a loop-iteration, traversal, or run-duration limit. Inspect and remove the malformed pre-1.0 artifact before retrying.";
        return new FormatException(message, innerException);
    }

    private static string ArtifactLabel(string artifactName, string? path) => path is null ? artifactName : $"{artifactName} `{path}`";
}
