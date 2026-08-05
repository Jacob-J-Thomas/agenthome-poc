namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Identifies an explicitly registered instruction-source convention without loading its contents.</summary>
public enum ContextualRoleInstructionSourceKind
{
    /// <summary>An undefined source kind that is never valid.</summary>
    Unknown = 0,
    /// <summary>Instructions authored directly in the contextual-role artifact.</summary>
    RoleArtifact = 1,
    /// <summary>Instructions referenced through the established AGENTS.md convention.</summary>
    AgentsMarkdown = 2,
    /// <summary>Instructions referenced through the established .agent/ROLE.md convention.</summary>
    WorkspaceRoleMarkdown = 3
}
