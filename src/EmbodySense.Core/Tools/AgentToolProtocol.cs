using System.Text;
using System.Text.Json;
using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Core.Tools;

public static class AgentToolProtocol
{
    public const string ToolBlockName = "embodysense-tool";
    public const string ToolsBlockName = "embodysense-tools";

    public static string SystemInstructions => """
        EmbodySense governed tools are available in this harness turn.

        Request tools by returning one or more fenced JSON blocks. Use `embodysense-tool` for a single request or `embodysense-tools` for an array of requests.

        Single request example:

        ```embodysense-tool
        {"tool":"read","path":"workspace/shared/notes.md"}
        ```

        Supported tools:
        - `list`: list a directory. Requires list permission for the target directory.
        - `read`: read a file. Requires read permission for the containing directory.
        - `search`: search a file or directory for a text pattern. Requires read permission.
        - `write`: create or replace a file. Requires create or modify permission for the containing directory.
        - `append`: append text to a file, creating it if needed. Requires create or append permission for the containing directory.
        - `delete`: delete a file or directory. Requires delete permission.

        Request fields:
        - `tool` or `command`: one of the supported tool names.
        - `path` or `targetPath`: workspace-relative or absolute target path.
        - `content` or `text`: text for `write` and `append`.
        - `pattern` or `query`: text pattern for `search`.

        All tool requests are resolved against the active workspace, checked against `.agent/permissions.json`, routed through human approval when required, and written to the audit log. Missing or unmatched policy requires human approval. Explicit denied policy does not run. Do not claim a tool action succeeded until a tool result says it succeeded.
        """;

    public static IReadOnlyList<ToolRequest> ParseRequests(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var requests = new List<ToolRequest>();

        foreach (var block in ExtractToolBlocks(text))
        {
            requests.AddRange(ParseBlock(block));
        }

        return requests;
    }

    public static string FormatResults(IReadOnlyList<ToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var builder = new StringBuilder();
        builder.AppendLine("EmbodySense tool results:");

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

        builder.AppendLine("Use these results to continue the task. Request another tool only if needed.");
        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<ToolRequest> ParseBlock(ToolBlock block)
    {
        using var document = JsonDocument.Parse(block.Json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(ParseRequestElement).ToList();
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "requests", out var requestsElement) && requestsElement.ValueKind == JsonValueKind.Array)
        {
            return requestsElement.EnumerateArray().Select(ParseRequestElement).ToList();
        }

        return [ParseRequestElement(root)];
    }

    private static ToolRequest ParseRequestElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Tool request must be a JSON object.");
        }

        var commandText = GetRequiredString(element, "tool", "command");
        var targetPath = GetRequiredString(element, "path", "targetPath", "target");
        var content = GetOptionalString(element, "content", "text");
        var pattern = GetOptionalString(element, "pattern", "query");

        if (!Enum.TryParse<ToolCommand>(commandText, ignoreCase: true, out var command) || !Enum.IsDefined(command))
        {
            throw new FormatException($"Unsupported tool command: {commandText}");
        }

        return new ToolRequest(command, targetPath, content, pattern);
    }

    private static IReadOnlyList<ToolBlock> ExtractToolBlocks(string text)
    {
        var blocks = new List<ToolBlock>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var info = trimmed[3..].Trim();
            if (!IsToolBlock(info))
            {
                continue;
            }

            var json = new StringBuilder();
            i++;

            while (i < lines.Length && lines[i].Trim() != "```")
            {
                json.AppendLine(lines[i]);
                i++;
            }

            blocks.Add(new ToolBlock(info, json.ToString()));
        }

        return blocks;
    }

    private static bool IsToolBlock(string info)
    {
        return string.Equals(info, ToolBlockName, StringComparison.OrdinalIgnoreCase) || string.Equals(info, ToolsBlockName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRequiredString(JsonElement element, params string[] names)
    {
        var value = GetOptionalString(element, names);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"Tool request requires one of: {string.Join(", ", names)}.");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var item in element.EnumerateObject())
        {
            if (string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = item.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string Indent(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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

    private sealed record ToolBlock(string Info, string Json);
}
