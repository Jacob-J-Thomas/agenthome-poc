using System.Text;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Formats tool results.
/// </summary>
public static class ToolResultFormatter
{
    private const string Header = "EmbodySense dynamic tool results:";
    private const string ContinuationInstruction = "Use these results to continue the task. Request another dynamic tool only if needed.";
    private const string MetadataTruncationMarker = "...[truncated]";
    private const string OutputTruncationMarker = "    [tool output truncated to preserve all result evidence]";
    /// <summary>
    /// Maximum characters exposed to the model in one formatted tool-result block.
    /// </summary>
    public const int MaxFormattedCharacters = 64_000;
    private static readonly string _finalTruncationMarker = $"[formatted tool results truncated to the {MaxFormattedCharacters}-character limit]";
    private static readonly int _minimumMetadataValueCharacters = MetadataTruncationMarker.Length + 1;

    /// <summary>
    /// Formats governed tool results for model-visible continuation context.
    /// </summary>
    /// <param name="results">The ordered governed tool results to project.</param>
    /// <returns>A bounded ordered projection that preserves essential evidence for every accepted result before allocating output text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="results"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the minimum evidence envelope for the supplied result count cannot fit within <see cref="MaxFormattedCharacters"/>.</exception>
    public static string FormatResults(IReadOnlyList<ToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var ordinary = FormatOrdinaryResults(results);
        return ordinary.Length <= MaxFormattedCharacters ? ordinary : FormatBoundedResults(results);
    }

    private static string FormatOrdinaryResults(IReadOnlyList<ToolResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);

        foreach (var result in results)
        {
            builder.AppendLine(FormatResultPrefix(result, MaxFormattedCharacters));
            builder.AppendLine(Indent(result.OutputText));
        }

        builder.AppendLine(ContinuationInstruction);
        return builder.ToString().TrimEnd();
    }

    private static string FormatBoundedResults(IReadOnlyList<ToolResult> results)
    {
        var metadataValueLimit = FindMetadataValueLimit(results);
        var resultPrefixes = results.Select(result => FormatResultPrefix(result, metadataValueLimit)).ToArray();
        var fixedLength = CalculateFixedLength(resultPrefixes, outputBodyLength: 0);
        var remainingOutputBudget = MaxFormattedCharacters - fixedLength;
        var outputBodies = new string[results.Count];

        for (var index = 0; index < results.Count; index++)
        {
            var remainingResultCount = results.Count - index;
            var outputBudget = remainingOutputBudget / remainingResultCount;
            outputBodies[index] = FormatOutputWithinBudget(results[index].OutputText, outputBudget);
            remainingOutputBudget -= outputBodies[index].Length;
        }

        var segments = new List<string>(3 + (results.Count * 2)) { Header };
        for (var index = 0; index < results.Count; index++)
        {
            segments.Add(resultPrefixes[index]);
            segments.Add(outputBodies[index]);
        }

        segments.Add(ContinuationInstruction);
        segments.Add(_finalTruncationMarker);
        return string.Join(Environment.NewLine, segments);
    }

    private static int FindMetadataValueLimit(IReadOnlyList<ToolResult> results)
    {
        var lower = _minimumMetadataValueCharacters;
        if (!MetadataEnvelopeFits(results, lower))
        {
            throw new ArgumentException($"The minimum evidence envelope for {results.Count} tool results exceeds the {MaxFormattedCharacters}-character limit.", nameof(results));
        }

        var upper = MaxFormattedCharacters;
        while (lower < upper)
        {
            var candidate = lower + ((upper - lower + 1) / 2);
            if (MetadataEnvelopeFits(results, candidate))
            {
                lower = candidate;
            }
            else
            {
                upper = candidate - 1;
            }
        }

        return lower;
    }

    private static bool MetadataEnvelopeFits(IReadOnlyList<ToolResult> results, int metadataValueLimit)
    {
        var resultPrefixes = results.Select(result => FormatResultPrefix(result, metadataValueLimit)).ToArray();
        var minimumOutputLength = results.Count * OutputTruncationMarker.Length;
        return CalculateFixedLength(resultPrefixes, minimumOutputLength) <= MaxFormattedCharacters;
    }

    private static int CalculateFixedLength(IReadOnlyList<string> resultPrefixes, int outputBodyLength)
    {
        var segmentCount = 3 + (resultPrefixes.Count * 2);
        var separatorLength = (segmentCount - 1) * Environment.NewLine.Length;
        return Header.Length + resultPrefixes.Sum(prefix => prefix.Length) + outputBodyLength + ContinuationInstruction.Length + _finalTruncationMarker.Length + separatorLength;
    }

    private static string FormatResultPrefix(ToolResult result, int metadataValueLimit)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"- request_id: {FormatMetadataValue(result.RequestId, metadataValueLimit)}");
        builder.AppendLine($"  tool: {ToolCommandFormatter.Format(result.Request.Command)}");
        builder.AppendLine($"  outcome: {FormatOutcome(result.Outcome)}");
        AppendRetention(builder, result.Retention, metadataValueLimit);
        builder.AppendLine($"  target_path: {FormatMetadataValue(result.Request.TargetPath, metadataValueLimit)}");
        builder.AppendLine($"  resolved_path: {FormatMetadataValue(result.ResolvedPath, metadataValueLimit)}");
        builder.Append("  output:");
        return builder.ToString();
    }

    private static string Indent(string text)
    {
        var formatted = text.Length <= MaxFormattedCharacters
            ? text
            : TakeSafePrefix(text, MaxFormattedCharacters) + Environment.NewLine + $"[tool output truncated after {MaxFormattedCharacters} characters]";
        var lines = formatted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => "    " + line));
    }

    private static string FormatOutputWithinBudget(string text, int budget)
    {
        var formatted = Indent(text);
        if (formatted.Length <= budget)
        {
            return formatted;
        }

        var suffix = Environment.NewLine + OutputTruncationMarker;
        if (budget < suffix.Length)
        {
            return OutputTruncationMarker;
        }

        return TakeSafePrefix(formatted, budget - suffix.Length) + suffix;
    }

    private static string FormatMetadataValue(string? value, int limit)
    {
        value ??= "";
        if (value.Length <= limit)
        {
            return value;
        }

        return TakeSafePrefix(value, limit - MetadataTruncationMarker.Length) + MetadataTruncationMarker;
    }

    private static string TakeSafePrefix(string value, int count)
    {
        if (count <= 0)
        {
            return "";
        }

        count = Math.Min(count, value.Length);
        if (char.IsHighSurrogate(value[count - 1]))
        {
            count--;
        }

        return value[..count];
    }

    private static string FormatOutcome(ToolExecutionOutcome outcome)
    {
        return outcome.ToString().ToLowerInvariant();
    }

    private static void AppendRetention(StringBuilder builder, ToolResultRetentionReference? retention, int metadataValueLimit)
    {
        if (retention?.Status == ToolResultRetentionStatus.Retained)
        {
            builder.AppendLine($"  full_response_manifest: {FormatMetadataValue(retention.ManifestPath, metadataValueLimit)}");
            builder.AppendLine($"  full_response_sha256: {FormatMetadataValue(retention.ContentSha256, metadataValueLimit)}");
            builder.AppendLine($"  full_response_size: {retention.CharacterCount} characters / {retention.Utf8ByteCount} UTF-8 bytes / {retention.ChunkCount} chunks");
            builder.AppendLine($"  full_response_retention: {FormatMetadataValue(retention.Detail, metadataValueLimit)}");
            return;
        }

        builder.AppendLine("  full_response_manifest: unavailable");
        builder.AppendLine($"  full_response_retention: {FormatMetadataValue(retention?.Detail ?? "The caller did not provide a durable full-response reference.", metadataValueLimit)}");
    }
}
