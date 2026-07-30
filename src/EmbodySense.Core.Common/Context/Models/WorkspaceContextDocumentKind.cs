namespace EmbodySense.Core.Common.Context.Models;

/// <summary>
/// Identifies the supported workspace context document kind values.
/// </summary>
public enum WorkspaceContextDocumentKind
{
    /// <summary>
    /// Identifies the unknown workspace context document kind.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the role instruction workspace context document kind.
    /// </summary>
    RoleInstruction = 1,
    /// <summary>
    /// Identifies the contextual state workspace context document kind.
    /// </summary>
    ContextualState = 2,
    /// <summary>
    /// Identifies the agent identity workspace context document kind.
    /// </summary>
    AgentIdentity = 3
}
