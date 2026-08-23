namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Identifies a retained-handle path whose exact native terminal name differs from its governed name.</summary>
internal sealed class WorkspaceActionExactNameMismatchException(string message) : IOException(message);
