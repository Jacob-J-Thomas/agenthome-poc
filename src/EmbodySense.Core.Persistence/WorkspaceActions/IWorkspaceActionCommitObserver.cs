using EmbodySense.Core.Persistence.WorkspaceActions.Models;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Observes bounded last-safe commit points for deterministic adversarial race tests.</summary>
public interface IWorkspaceActionCommitObserver
{
    /// <summary>Observes one value-free point after revalidation and before the native target mutation.</summary>
    Task ObserveAsync(WorkspaceActionCommitPoint point, string beforeEvidenceId, CancellationToken cancellationToken = default);
}
