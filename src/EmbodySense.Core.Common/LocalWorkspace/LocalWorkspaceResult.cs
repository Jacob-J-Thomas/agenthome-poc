namespace EmbodySense.Core.Common.LocalWorkspace;

public sealed record LocalWorkspaceResult(string Text, IReadOnlyDictionary<string, object?> Metadata);
