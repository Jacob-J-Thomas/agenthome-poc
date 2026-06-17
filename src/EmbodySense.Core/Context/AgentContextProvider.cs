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
        var workspaceInstructionsPath = FindWorkspaceInstructionsPath(paths.RootPath);
        if (workspaceInstructionsPath is not null)
        {
            var content = await File.ReadAllTextAsync(workspaceInstructionsPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                sections.Add(FormatWorkspaceInstructionsSection(content));
            }
        }

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

            sections.Add(FormatAgentDocumentSection(fileName, content));
        }

        if (sections.Count == 0)
        {
            return [];
        }

        var builder = new StringBuilder();
        builder.AppendLine("EmbodySense loaded the following startup context from workspace instructions and workspace agent documents.");
        builder.AppendLine("Follow the current user request and higher-priority instructions first.");
        builder.AppendLine("Durable memory policy: treat `.agent/MEMORY.md` as the primary place to store, update, create, and retrieve most memories.");
        builder.AppendLine("Query conversation history only for transcript-specific evidence such as exact wording, chronology, or context that has not yet been distilled into `.agent/MEMORY.md`.");
        builder.AppendLine();
        builder.AppendLine(string.Join(Environment.NewLine, sections));

        return [LlmMessage.System(builder.ToString().Trim())];
    }

    private static string? FindWorkspaceInstructionsPath(string rootPath)
    {
        var instructionsPath = Path.Combine(Path.GetFullPath(rootPath), "AGENTS.md");
        return File.Exists(instructionsPath) ? instructionsPath : null;
    }

    private static string FormatAgentDocumentSection(string fileName, string content)
    {
        return FormatSection($".agent/{fileName}", content);
    }

    private static string FormatWorkspaceInstructionsSection(string content)
    {
        return FormatSection("AGENTS.md", content);
    }

    private static string FormatSection(string displayPath, string content)
    {
        var normalized = content.Trim();
        if (normalized.Length > MaxFileCharacters)
        {
            normalized = normalized[..MaxFileCharacters] + Environment.NewLine + "[truncated]";
        }

        return $"## {displayPath}{Environment.NewLine}{normalized}";
    }
}
