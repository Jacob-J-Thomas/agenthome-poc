using System.Text.Json;

namespace EmbodySense.Core.Persistence.Loops;

internal static class CustomLoopJsonDepthPolicy
{
    // Operation receipts are currently flat scalar records. This leaves ample shape-evolution headroom while bounding hostile or corrupt nesting.
    internal const int ShallowReceiptMaximumDepth = 32;

    // Canonical run artifacts contain bounded nested context, evidence, and projections and therefore use the larger persistence ceiling.
    internal const int CanonicalRunArtifactMaximumDepth = 64;

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

    internal static FormatException SerializationDepthException(string artifactName, int maximumDepth, JsonException exception, string? path = null)
    {
        return DepthException(ArtifactLabel(artifactName, path), maximumDepth, exception);
    }

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
