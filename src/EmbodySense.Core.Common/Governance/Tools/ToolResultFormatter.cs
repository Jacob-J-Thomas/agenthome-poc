using System.Text;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Governance.Tools;

public static class ToolResultFormatter
{
    public const int MaxFormattedCharacters = 64_000;
    private static readonly string FinalTruncationMarker = $"[formatted tool results truncated to the {MaxFormattedCharacters}-character limit]";

    public static string FormatResults(IReadOnlyList<ToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var builder = new StringBuilder();
        builder.AppendLine("EmbodySense dynamic tool results:");

        foreach (var result in results)
        {
            builder.AppendLine($"- request_id: {result.RequestId}");
            builder.AppendLine($"  tool: {ToolCommandFormatter.Format(result.Request.Command)}");
            builder.AppendLine($"  target_path: {result.Request.TargetPath}");
            builder.AppendLine($"  resolved_path: {result.ResolvedPath}");
            builder.AppendLine($"  outcome: {FormatOutcome(result.Outcome)}");
            builder.AppendLine("  output:");
            builder.AppendLine(Indent(result.OutputText));
        }

        builder.AppendLine("Use these results to continue the task. Request another dynamic tool only if needed.");
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

        var marker = Environment.NewLine + FinalTruncationMarker;
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
}
