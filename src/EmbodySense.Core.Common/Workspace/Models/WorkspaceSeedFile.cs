namespace EmbodySense.Core.Common.Workspace.Models;

/// <summary>
/// Represents a workspace seed file.
/// </summary>
/// <param name="Path">The workspace-relative path governed or materialized by the value.</param>
/// <param name="Content">The exact content.</param>
/// <param name="Overwrite">Whether an existing destination may be replaced.</param>
public sealed record WorkspaceSeedFile(string Path, string Content, bool Overwrite);
