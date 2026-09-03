namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies one legal operator disposition of a current reconciliation assessment.</summary>
public enum GovernedLoopEffectReconciliationDispositionKind
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>Accept authoritative proof that no effect was applied.</summary>
    AcceptProvedNotApplied = 1,
    /// <summary>Accept authoritative proof that the effect was applied with a known outcome.</summary>
    AcceptProvedApplied = 2,
    /// <summary>Quarantine the unresolved effect without a successor.</summary>
    QuarantineUnresolved = 3,
}
