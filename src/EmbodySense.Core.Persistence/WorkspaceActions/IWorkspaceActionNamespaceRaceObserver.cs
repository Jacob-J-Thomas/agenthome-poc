using EmbodySense.Core.Persistence.WorkspaceActions.Models;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Provides deterministic adversarial observation immediately around a native namespace system call.</summary>
public interface IWorkspaceActionNamespaceRaceObserver
{
    /// <summary>Observes one exact race window without receiving paths, handles, content, or mutable host state.</summary>
    Task ObserveAsync(
        WorkspaceActionNamespaceRacePoint point,
        string beforeEvidenceId,
        CancellationToken cancellationToken = default);
}
