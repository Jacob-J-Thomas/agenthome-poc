using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Context.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Context;

/// <summary>
/// Builds startup model context from workspace role instructions, durable identity, and lower-authority state.
/// </summary>
public sealed class AgentContextProvider : IAgentContextProvider
{
    private const int MaxFileCharacters = 12_000;

    /// <summary>
    /// Identifies the header applied to contextual state that must not override system instructions.
    /// </summary>
    internal const string ContextualStateClassification = "Lower-authority contextual state";
    private readonly IWorkspaceContextStore _contextStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentContextProvider"/> type.
    /// </summary>
    /// <param name="contextStore">The context store.</param>
    public AgentContextProvider(IWorkspaceContextStore contextStore)
    {
        ArgumentNullException.ThrowIfNull(contextStore);

        _contextStore = contextStore;
    }

    /// <summary>
    /// Loads workspace startup documents and projects them into ordered model messages.
    /// </summary>
    /// <param name="paths">The resolved paths for the workspace whose context is being loaded.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The startup messages, or an empty list when no nonblank documents are available.</returns>
    public async Task<IReadOnlyList<LlmMessage>> LoadAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var documents = await _contextStore.LoadDocumentsAsync(paths, cancellationToken);
        var availableDocuments = documents.Where(document => !string.IsNullOrWhiteSpace(document.Content)).ToArray();
        if (availableDocuments.Length == 0)
        {
            return [];
        }

        var messages = new List<LlmMessage>
        {
            LlmMessage.System("""
                EmbodySense loaded startup context from trusted role instructions, durable agent identity, and lower-authority workspace state.
                Follow the current user request and higher-priority instructions first.
                Durable memory policy: treat `.agent/MEMORY.md` as the primary place to store, update, create, and retrieve most memories.
                Query conversation history only for transcript-specific evidence such as exact wording, chronology, or context that has not yet been distilled into `.agent/MEMORY.md`.
                """)
        };
        foreach (var document in availableDocuments)
        {
            var section = FormatSection(document.Kind, document.DisplayPath, document.Content);
            // Role and identity documents are trusted instructions. Mutable workspace state is user context
            // so it cannot silently acquire the same authority.
            messages.Add(document.Kind is WorkspaceContextDocumentKind.RoleInstruction or WorkspaceContextDocumentKind.AgentIdentity
                ? LlmMessage.System(section)
                : LlmMessage.User(section));
        }

        return messages;
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
            WorkspaceContextDocumentKind.ContextualState => ContextualStateClassification,
            _ => "Workspace context"
        };
        return $"## {classification}: {displayPath}{Environment.NewLine}{normalized}";
    }
}
