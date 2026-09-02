namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the bounded operator-visible lifecycle posture of one reconciliation case summary.</summary>
public enum GovernedLoopEffectReconciliationCaseSummaryStatus
{
    /// <summary>No supported summary posture was established.</summary>
    Unknown = 0,

    /// <summary>The case is open and has no current assessment.</summary>
    Open = 1,

    /// <summary>The case has a current assessment but no disposition.</summary>
    Assessed = 2,

    /// <summary>An applied or not-applied assessment was accepted but has no committed resolution.</summary>
    Accepted = 3,

    /// <summary>An inconclusive or conflicting assessment was explicitly quarantined without an effect successor.</summary>
    Quarantined = 4,

    /// <summary>An accepted resolution and its exact effect outcome were atomically committed.</summary>
    Resolved = 5,
}
