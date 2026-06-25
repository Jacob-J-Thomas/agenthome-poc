using System.Text;
using EmbodySense.Core.Application.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public static class ToolResultFormatter
{
    private const int MaxOutputCharacters = 64_000;

    public static string FormatResults(IReadOnlyList<ToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var builder = new StringBuilder();
        builder.AppendLine("EmbodySense dynamic tool results:");

        foreach (var result in results)
        {
            builder.AppendLine($"- request_id: {result.RequestId}");
            builder.AppendLine($"  tool: {FormatCommand(result.Request.Command)}");
            builder.AppendLine($"  target_path: {result.Request.TargetPath}");
            builder.AppendLine($"  resolved_path: {result.ResolvedPath}");
            builder.AppendLine($"  outcome: {FormatOutcome(result.Outcome)}");
            builder.AppendLine("  output:");
            builder.AppendLine(Indent(result.OutputText));
        }

        builder.AppendLine("Use these results to continue the task. Request another dynamic tool only if needed.");
        return builder.ToString().TrimEnd();
    }

    private static string Indent(string text)
    {
        var formatted = text.Length <= MaxOutputCharacters
            ? text
            : text[..MaxOutputCharacters] + Environment.NewLine + $"[tool output truncated after {MaxOutputCharacters} characters]";
        var lines = formatted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => "    " + line));
    }

    private static string FormatCommand(ToolCommand command)
    {
        return command.ToString().ToLowerInvariant();
    }

    private static string FormatOutcome(ToolExecutionOutcome outcome)
    {
        return outcome.ToString().ToLowerInvariant();
    }
}
