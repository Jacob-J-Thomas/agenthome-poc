namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies the exact external-effect state reported by one observation.</summary>
public enum GovernedLoopEffectReconciliationObservedOutcome
{
    /// <summary>No supported external state was observed.</summary>
    Unknown = 0,

    /// <summary>Evidence proves that the external effect was not applied.</summary>
    NotApplied = 1,

    /// <summary>Evidence proves that the effect was applied and succeeded.</summary>
    AppliedSucceeded = 2,

    /// <summary>Evidence proves that the effect was applied and failed.</summary>
    AppliedFailed = 3,

    /// <summary>Evidence proves application but not the resulting success or failure.</summary>
    AppliedOutcomeUnknown = 4
}
