namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies the only outcomes an accepted immutable reconciliation resolution may publish.</summary>
public enum GovernedLoopEffectReconciliationResolutionOutcome
{
    /// <summary>No supported resolution outcome was established.</summary>
    Unknown = 0,
    /// <summary>Authoritative evidence proves that the effect was not applied.</summary>
    NotApplied = 1,
    /// <summary>Authoritative evidence proves that the applied effect succeeded.</summary>
    Succeeded = 2,
    /// <summary>Authoritative evidence proves that the applied effect failed.</summary>
    Failed = 3,
}
