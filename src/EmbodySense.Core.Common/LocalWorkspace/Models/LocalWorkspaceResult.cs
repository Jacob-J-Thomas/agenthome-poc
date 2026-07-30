namespace EmbodySense.Core.Common.LocalWorkspace.Models;

/// <summary>
/// Represents a local workspace result.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="Metadata">Additional metadata retained with the value.</param>
public sealed record LocalWorkspaceResult(string Text, IReadOnlyDictionary<string, object?> Metadata);
