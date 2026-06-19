using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Core.Persistence.Workspace;

public sealed class WorkspaceContextStore : IWorkspaceContextStore
{
    private static readonly string[] AgentContextFiles =
    [
        "AGENT.md",
        "SOUL.md",
        "PERSONALITY.md",
        "CONTEXT.md",
        "MEMORY.md",
        "models.json"
    ];

    public async Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var documents = new List<WorkspaceContextDocument>();
        var workspaceInstructionsPath = FindWorkspaceInstructionsPath(paths.RootPath);
        if (workspaceInstructionsPath is not null)
        {
            var content = await File.ReadAllTextAsync(workspaceInstructionsPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                documents.Add(new WorkspaceContextDocument("AGENTS.md", content));
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
            if (!string.IsNullOrWhiteSpace(content))
            {
                documents.Add(new WorkspaceContextDocument($".agent/{fileName}", content));
            }
        }

        return documents;
    }

    private static string? FindWorkspaceInstructionsPath(string rootPath)
    {
        var instructionsPath = Path.Combine(Path.GetFullPath(rootPath), "AGENTS.md");
        return File.Exists(instructionsPath) ? instructionsPath : null;
    }
}
