namespace EmbodySense.Core.Application.Governance.Tools;

public sealed record LocalWorkspaceResult(string Text, IReadOnlyDictionary<string, object?> Metadata);
