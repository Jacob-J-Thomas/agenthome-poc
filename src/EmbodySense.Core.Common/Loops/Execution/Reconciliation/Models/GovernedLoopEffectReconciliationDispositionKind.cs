namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies the one authoritative disposition of the current reconciliation assessment.</summary>
public enum GovernedLoopEffectReconciliationDispositionKind
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>Accept the proof that no external effect was applied.</summary>
    AcceptProvedNotApplied = 1,

    /// <summary>Accept the proof that the external effect was applied with a known outcome.</summary>
    AcceptProvedApplied = 2,

    /// <summary>Retain the attempt as unresolved and prohibit an effect successor.</summary>
    QuarantineUnresolved = 3
}
