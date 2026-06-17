using System.Text;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Context;

public sealed class AgentContextProvider
{
    private const int MaxFileCharacters = 12_000;
    private static readonly string[] AgentContextFiles =
    [
        "AGENT.md",
        "SOUL.md",
        "PERSONALITY.md",
        "CONTEXT.md",
        "MEMORY.md",
        "models.json"
    ];

    public async Task<IReadOnlyList<LlmMessage>> LoadAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var sections = new List<string>();
        foreach (var fileName in AgentContextFiles)
        {
            var path = paths.AgentFile(fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            sections.Add(FormatSection(fileName, content));
        }

        if (sections.Count == 0)
        {
            return [];
        }

        var builder = new StringBuilder();
        builder.AppendLine("EmbodySense loaded the following startup context from workspace agent documents.");
        builder.AppendLine("Use it as durable local context, but follow the current user request and higher-priority instructions first."); 
        builder.AppendLine();
        builder.AppendLine(string.Join(Environment.NewLine, sections));

        return [LlmMessage.System(builder.ToString().Trim())];
    }

    private static string FormatSection(string fileName, string content)
    {
        var normalized = content.Trim();
        if (normalized.Length > MaxFileCharacters)
        {
            normalized = normalized[..MaxFileCharacters] + Environment.NewLine + "[truncated]";
        }

        return $"## .agent/{fileName}{Environment.NewLine}{normalized}";
    }
}
