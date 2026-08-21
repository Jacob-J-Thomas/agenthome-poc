namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Signals that a finite workspace-action evidence kind requires authenticated cleanup before retry.</summary>
internal sealed class WorkspaceActionEvidenceCapacityException : InvalidOperationException
{
    public WorkspaceActionEvidenceCapacityException()
        : base("Workspace action evidence capacity is exhausted.")
    {
    }
}
