namespace EmbodySense.Core.Common.LocalWorkspace.Models;

public sealed record LocalWorkspaceResult(string Text, IReadOnlyDictionary<string, object?> Metadata);
