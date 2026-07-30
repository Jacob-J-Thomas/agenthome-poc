using System.Text;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Formats tool results.
/// </summary>
public static class ToolResultFormatter
{
    /// <summary>
    /// Maximum characters exposed to the model in one formatted tool-result block.
    /// </summary>
    public const int MaxFormattedCharacters = 64_000;
    private static readonly string _finalTruncationMarker = $"[formatted tool results truncated to the {MaxFormattedCharacters}-character limit]";

    /// <summary>
    /// Formats governed tool results for model-visible continuation context.
    /// </summary>
    /// <param name="results">The ordered governed tool results to project.</param>
    /// <returns>A bounded prefix of the ordered projection. Global truncation can omit some or all fields for later results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="results"/> is <see langword="null"/>.</exception>
    public static string FormatResults(IReadOnlyList<ToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var builder = new StringBuilder();
        builder.AppendLine("EmbodySense dynamic tool results:");

        foreach (var result in results)
        {
            builder.AppendLine($"- request_id: {result.RequestId}");
            builder.AppendLine($"  tool: {ToolCommandFormatter.Format(result.Request.Command)}");
            builder.AppendLine($"  outcome: {FormatOutcome(result.Outcome)}");
            AppendRetention(builder, result.Retention);
            builder.AppendLine($"  target_path: {result.Request.TargetPath}");
            builder.AppendLine($"  resolved_path: {result.ResolvedPath}");
            builder.AppendLine("  output:");
            builder.AppendLine(Indent(result.OutputText));
        }

        builder.AppendLine("Use these results to continue the task. Request another dynamic tool only if needed.");
        // TODO(#143): Reserve essential identity, outcome, path, and retention evidence for every result before allocating output text.
        return ApplyFinalLimit(builder.ToString().TrimEnd());
    }

    private static string Indent(string text)
    {
        var formatted = text.Length <= MaxFormattedCharacters
            ? text
            : text[..MaxFormattedCharacters] + Environment.NewLine + $"[tool output truncated after {MaxFormattedCharacters} characters]";
        var lines = formatted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => "    " + line));
    }

    private static string ApplyFinalLimit(string formatted)
    {
        if (formatted.Length <= MaxFormattedCharacters)
        {
            return formatted;
        }

        var marker = Environment.NewLine + _finalTruncationMarker;
        var retainedCharacterCount = MaxFormattedCharacters - marker.Length;
        if (char.IsHighSurrogate(formatted[retainedCharacterCount - 1]))
        {
            retainedCharacterCount--;
        }

        return formatted[..retainedCharacterCount] + marker;
    }

    private static string FormatOutcome(ToolExecutionOutcome outcome)
    {
        return outcome.ToString().ToLowerInvariant();
    }

    private static void AppendRetention(StringBuilder builder, ToolResultRetentionReference? retention)
    {
        if (retention?.Status == ToolResultRetentionStatus.Retained)
        {
            builder.AppendLine($"  full_response_manifest: {retention.ManifestPath}");
            builder.AppendLine($"  full_response_sha256: {retention.ContentSha256}");
            builder.AppendLine($"  full_response_size: {retention.CharacterCount} characters / {retention.Utf8ByteCount} UTF-8 bytes / {retention.ChunkCount} chunks");
            builder.AppendLine($"  full_response_retention: {retention.Detail}");
            return;
        }

        builder.AppendLine("  full_response_manifest: unavailable");
        builder.AppendLine($"  full_response_retention: {retention?.Detail ?? "The caller did not provide a durable full-response reference."}");
    }
}
