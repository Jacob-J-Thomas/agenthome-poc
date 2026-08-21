using EmbodySense.Core.Persistence.WorkspaceActions.Models;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Observes value-free native durability windows for crash testing and host telemetry.</summary>
public interface IWorkspaceActionDurabilityObserver
{
    /// <summary>Observes one published-but-not-yet-complete evidence point without receiving paths or content.</summary>
    Task ObserveAsync(
        WorkspaceActionDurabilityPoint point,
        string beforeEvidenceId,
        string effectId,
        CancellationToken cancellationToken = default);
}
