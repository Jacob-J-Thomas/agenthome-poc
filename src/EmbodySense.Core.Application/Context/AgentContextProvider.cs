using System.Text;
using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Context;

public sealed class AgentContextProvider : IAgentContextProvider
{
    private const int MaxFileCharacters = 12_000;
    private readonly IWorkspaceContextStore _contextStore;

    public AgentContextProvider(IWorkspaceContextStore contextStore)
    {
        ArgumentNullException.ThrowIfNull(contextStore);

        _contextStore = contextStore;
    }

    public async Task<IReadOnlyList<LlmMessage>> LoadAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var sections = new List<string>();
        var documents = await _contextStore.LoadDocumentsAsync(paths, cancellationToken);
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.Content))
            {
                continue;
            }

            sections.Add(FormatSection(document.Kind, document.DisplayPath, document.Content));
        }

        if (sections.Count == 0)
        {
            return [];
        }

        var builder = new StringBuilder();
        builder.AppendLine("EmbodySense loaded the following startup context from trusted role instructions, durable agent identity, and lower-authority workspace state.");
        builder.AppendLine("Follow the current user request and higher-priority instructions first.");
        builder.AppendLine("Durable memory policy: treat `.agent/MEMORY.md` as the primary place to store, update, create, and retrieve most memories.");
        builder.AppendLine("Query conversation history only for transcript-specific evidence such as exact wording, chronology, or context that has not yet been distilled into `.agent/MEMORY.md`.");
        builder.AppendLine();
        builder.AppendLine(string.Join(Environment.NewLine, sections));

        return [LlmMessage.System(builder.ToString().Trim())];
    }

    private static string FormatSection(WorkspaceContextDocumentKind kind, string displayPath, string content)
    {
        var normalized = content.Trim();
        if (normalized.Length > MaxFileCharacters)
        {
            normalized = normalized[..MaxFileCharacters] + Environment.NewLine + $"[truncated after {MaxFileCharacters} characters]";
        }

        var classification = kind switch
        {
            WorkspaceContextDocumentKind.RoleInstruction => "Trusted role instruction",
            WorkspaceContextDocumentKind.AgentIdentity => "Trusted durable agent identity",
            WorkspaceContextDocumentKind.ContextualState => "Lower-authority contextual state",
            _ => "Workspace context"
        };
        return $"## {classification}: {displayPath}{Environment.NewLine}{normalized}";
    }
}
