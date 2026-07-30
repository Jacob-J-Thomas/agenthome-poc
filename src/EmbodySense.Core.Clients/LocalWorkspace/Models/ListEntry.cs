namespace EmbodySense.Core.Clients.LocalWorkspace.Models;

/// <summary>
/// Captures the canonical path and display metadata for one direct workspace child.
/// </summary>
/// <param name="Path">The canonical entry path.</param>
/// <param name="Name">The entry name rendered to the caller.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
internal sealed record ListEntry(string Path, string Name, bool IsDirectory);
