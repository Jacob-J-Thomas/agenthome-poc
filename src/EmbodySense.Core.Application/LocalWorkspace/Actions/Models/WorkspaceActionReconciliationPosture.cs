namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Identifies one proof-only workspace reconciliation posture.</summary>
public enum WorkspaceActionReconciliationPosture
{
    /// <summary>The available retained evidence was invalid or unavailable.</summary>
    Unknown = 0,

    /// <summary>The exact target is proved unchanged from retained before evidence.</summary>
    ProvedNotStarted = 1,

    /// <summary>One exact retained after outcome is proved at the target or quarantine.</summary>
    ProvedOutcomeObserved = 2,

    /// <summary>The retained evidence cannot conclusively distinguish the native outcome.</summary>
    Indeterminate = 3,
}
