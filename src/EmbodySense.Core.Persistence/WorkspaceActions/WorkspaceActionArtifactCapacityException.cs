namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Signals that a finite private workspace-action artifact root requires authenticated cleanup before retry.</summary>
internal sealed class WorkspaceActionArtifactCapacityException(string message) : InvalidOperationException(message);
