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
    private const int MinimumOutputExcerptCharacters = 256;
    private const int MinimumRetentionDetailCharacters = 128;
    private const int MinimumTargetPathCharacters = 128;
    private const int MinimumResolvedPathCharacters = 192;
    /// <summary>
    /// Maximum characters exposed to the model in one formatted tool-result block.
    /// </summary>
    public const int MaxFormattedCharacters = 64_000;
    private static readonly string _finalTruncationMarker = $"[formatted tool results truncated to the {MaxFormattedCharacters}-character limit]";
    private static readonly FlexibleMetadataLimits _minimumFlexibleMetadataLimits = new(MinimumRetentionDetailCharacters, MinimumTargetPathCharacters, MinimumResolvedPathCharacters);
    private static readonly FlexibleMetadataLimits _unboundedFlexibleMetadataLimits = new(int.MaxValue, int.MaxValue, int.MaxValue);

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
            builder.AppendLine(FormatResultPrefix(result, _unboundedFlexibleMetadataLimits));
            builder.AppendLine(Indent(result.OutputText));
        }

        builder.AppendLine(ContinuationInstruction);
        return builder.ToString().TrimEnd();
    }

    private static string FormatBoundedResults(IReadOnlyList<ToolResult> results)
    {
        var minimumOutputLengths = results.Select(result => CalculateMinimumOutputBodyLength(result.OutputText)).ToArray();
        var metadataExpansion = FindFlexibleMetadataExpansion(results, minimumOutputLengths.Sum());
        var metadataLimits = ExpandFlexibleMetadataLimits(metadataExpansion);
        var resultPrefixes = results.Select(result => FormatResultPrefix(result, metadataLimits)).ToArray();
        var fixedLength = CalculateFixedLength(resultPrefixes, outputBodyLength: 0);
        var outputBudgets = AllocateOutputBudgets(results, minimumOutputLengths, MaxFormattedCharacters - fixedLength);
        var outputBodies = new string[results.Count];

        for (var index = 0; index < results.Count; index++)
        {
            outputBodies[index] = FormatOutputWithinBudget(results[index].OutputText, outputBudgets[index]);
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

    private static int FindFlexibleMetadataExpansion(IReadOnlyList<ToolResult> results, int minimumOutputBodyLength)
    {
        var lower = 0;
        if (!MetadataEnvelopeFits(results, _minimumFlexibleMetadataLimits, minimumOutputBodyLength))
        {
            throw new ArgumentException($"The minimum evidence envelope for {results.Count} tool results exceeds the {MaxFormattedCharacters}-character limit.", nameof(results));
        }

        var upper = MaxFormattedCharacters;
        while (lower < upper)
        {
            var candidate = lower + ((upper - lower + 1) / 2);
            if (MetadataEnvelopeFits(results, ExpandFlexibleMetadataLimits(candidate), minimumOutputBodyLength))
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

    private static FlexibleMetadataLimits ExpandFlexibleMetadataLimits(int expansion)
    {
        return new FlexibleMetadataLimits(
            MinimumRetentionDetailCharacters + expansion,
            MinimumTargetPathCharacters + expansion,
            MinimumResolvedPathCharacters + expansion);
    }

    private static bool MetadataEnvelopeFits(IReadOnlyList<ToolResult> results, FlexibleMetadataLimits metadataLimits, int minimumOutputBodyLength)
    {
        var resultPrefixes = results.Select(result => FormatResultPrefix(result, metadataLimits)).ToArray();
        return CalculateFixedLength(resultPrefixes, minimumOutputBodyLength) <= MaxFormattedCharacters;
    }

    private static int CalculateFixedLength(IReadOnlyList<string> resultPrefixes, int outputBodyLength)
    {
        var segmentCount = 3 + (resultPrefixes.Count * 2);
        var separatorLength = (segmentCount - 1) * Environment.NewLine.Length;
        return Header.Length + resultPrefixes.Sum(prefix => prefix.Length) + outputBodyLength + ContinuationInstruction.Length + _finalTruncationMarker.Length + separatorLength;
    }

    private static int CalculateMinimumOutputBodyLength(string text)
    {
        var excerpt = TakeSafePrefix(text, MinimumOutputExcerptCharacters);
        var formattedExcerpt = Indent(excerpt);
        return excerpt.Length == text.Length
            ? formattedExcerpt.Length
            : formattedExcerpt.Length + Environment.NewLine.Length + OutputTruncationMarker.Length;
    }

    private static int[] AllocateOutputBudgets(IReadOnlyList<ToolResult> results, IReadOnlyList<int> minimumOutputLengths, int totalOutputBudget)
    {
        var outputBudgets = minimumOutputLengths.ToArray();
        var remainingBudget = totalOutputBudget - outputBudgets.Sum();
        if (remainingBudget <= 0)
        {
            return outputBudgets;
        }

        var desiredOutputLengths = results.Select(result => Indent(result.OutputText).Length).ToArray();
        var additionalNeeds = desiredOutputLengths.Select((length, index) => Math.Max(0, length - outputBudgets[index])).ToArray();
        var lower = 0;
        var upper = additionalNeeds.Max();
        while (lower < upper)
        {
            var candidate = lower + ((upper - lower + 1) / 2);
            var required = additionalNeeds.Sum(need => Math.Min(need, candidate));
            if (required <= remainingBudget)
            {
                lower = candidate;
            }
            else
            {
                upper = candidate - 1;
            }
        }

        for (var index = 0; index < outputBudgets.Length; index++)
        {
            var allocated = Math.Min(additionalNeeds[index], lower);
            outputBudgets[index] += allocated;
            remainingBudget -= allocated;
        }

        for (var index = 0; index < outputBudgets.Length && remainingBudget > 0; index++)
        {
            if (outputBudgets[index] >= desiredOutputLengths[index])
            {
                continue;
            }

            outputBudgets[index]++;
            remainingBudget--;
        }

        return outputBudgets;
    }

    private static string FormatResultPrefix(ToolResult result, FlexibleMetadataLimits metadataLimits)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"- request_id: {result.RequestId}");
        builder.AppendLine($"  tool: {ToolCommandFormatter.Format(result.Request.Command)}");
        builder.AppendLine($"  outcome: {FormatOutcome(result.Outcome)}");
        AppendRetention(builder, result.Retention, metadataLimits.RetentionDetailCharacters);
        builder.AppendLine($"  target_path: {FormatMetadataValue(result.Request.TargetPath, metadataLimits.TargetPathCharacters)}");
        builder.AppendLine($"  resolved_path: {FormatMetadataValue(result.ResolvedPath, metadataLimits.ResolvedPathCharacters)}");
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
        if (count == 0)
        {
            return "";
        }

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

    private static void AppendRetention(StringBuilder builder, ToolResultRetentionReference? retention, int retentionDetailLimit)
    {
        if (retention?.Status == ToolResultRetentionStatus.Retained)
        {
            builder.AppendLine($"  full_response_manifest: {retention.ManifestPath}");
            builder.AppendLine($"  full_response_sha256: {retention.ContentSha256}");
            builder.AppendLine($"  full_response_size: {retention.CharacterCount} characters / {retention.Utf8ByteCount} UTF-8 bytes / {retention.ChunkCount} chunks");
            builder.AppendLine($"  full_response_retention: {FormatMetadataValue(retention.Detail, retentionDetailLimit)}");
            return;
        }

        builder.AppendLine("  full_response_manifest: unavailable");
        builder.AppendLine($"  full_response_retention: {FormatMetadataValue(retention?.Detail ?? "The caller did not provide a durable full-response reference.", retentionDetailLimit)}");
    }

    private readonly record struct FlexibleMetadataLimits(int RetentionDetailCharacters, int TargetPathCharacters, int ResolvedPathCharacters);
}
