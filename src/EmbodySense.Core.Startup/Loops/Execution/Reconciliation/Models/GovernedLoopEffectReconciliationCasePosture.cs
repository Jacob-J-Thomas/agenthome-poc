namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the public lifecycle posture of one immutable reconciliation case.</summary>
public enum GovernedLoopEffectReconciliationCasePosture
{
    /// <summary>No supported posture was established.</summary>
    Unknown = 0,
    /// <summary>The case awaits evidence or assessment.</summary>
    Open = 1,
    /// <summary>The case has a current assessment but no disposition.</summary>
    Assessed = 2,
    /// <summary>A conclusive assessment was accepted but not yet resolved.</summary>
    Accepted = 3,
    /// <summary>The unresolved case was explicitly quarantined.</summary>
    Quarantined = 4,
    /// <summary>An immutable resolution and effect successor were committed.</summary>
    Resolved = 5,
}
