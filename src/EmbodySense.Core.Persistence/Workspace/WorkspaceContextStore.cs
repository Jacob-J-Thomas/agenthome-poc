using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Workspace;

public sealed class WorkspaceContextStore : IWorkspaceContextStore
{
    private static readonly (string SourceId, string FileName, WorkspaceContextDocumentKind Kind)[] AgentContextFiles =
    [
        ("soul", "SOUL.md", WorkspaceContextDocumentKind.IdentityInstruction),
        ("personality", "PERSONALITY.md", WorkspaceContextDocumentKind.IdentityInstruction),
        ("context", "CONTEXT.md", WorkspaceContextDocumentKind.ContextualState),
        ("memory", "MEMORY.md", WorkspaceContextDocumentKind.ContextualState),
        ("models", "models.json", WorkspaceContextDocumentKind.ContextualState)
    ];

    public async Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var documents = new List<WorkspaceContextDocument>();
        var workspaceInstructionsPath = WorkspaceInstructionLocator.FindNearest(paths.RootPath);
        var expectedWorkspaceInstructionsPath = Path.Combine(paths.RootPath, "AGENTS.md");
        documents.Add(await ReadDocumentAsync(
            "nearest-agents",
            workspaceInstructionsPath is null ? "AGENTS.md" : WorkspaceInstructionLocator.GetDisplayPath(paths.RootPath, workspaceInstructionsPath),
            workspaceInstructionsPath ?? expectedWorkspaceInstructionsPath,
            WorkspaceContextDocumentKind.RoleInstruction,
            cancellationToken));

        var rolePath = WorkspaceRoleInstructionLocator.ResolvePath(paths);
        documents.Add(await ReadDocumentAsync(
            "role",
            WorkspaceRoleInstructionLocator.GetDisplayPath(rolePath),
            rolePath,
            WorkspaceContextDocumentKind.RoleInstruction,
            cancellationToken));

        foreach (var (sourceId, fileName, kind) in AgentContextFiles)
        {
            var path = paths.AgentFile(fileName);
            documents.Add(await ReadDocumentAsync(sourceId, $".agent/{fileName}", path, kind, cancellationToken));
        }

        return documents;
    }

    private static async Task<WorkspaceContextDocument> ReadDocumentAsync(
        string sourceId,
        string displayPath,
        string exactPath,
        WorkspaceContextDocumentKind kind,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(exactPath);
        if (!File.Exists(normalizedPath))
        {
            return new WorkspaceContextDocument(sourceId, displayPath, normalizedPath, kind, string.Empty, 0, "The context source file did not exist at admission.");
        }

        var content = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new WorkspaceContextDocument(sourceId, displayPath, normalizedPath, kind, string.Empty, content.Length, "The context source file was empty at admission.");
        }

        return new WorkspaceContextDocument(sourceId, displayPath, normalizedPath, kind, content, content.Length, null);
    }
}
