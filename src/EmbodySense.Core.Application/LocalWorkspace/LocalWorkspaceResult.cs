namespace EmbodySense.Core.Application.LocalWorkspace;

public sealed record LocalWorkspaceResult(string Text, IReadOnlyDictionary<string, object?> Metadata);
