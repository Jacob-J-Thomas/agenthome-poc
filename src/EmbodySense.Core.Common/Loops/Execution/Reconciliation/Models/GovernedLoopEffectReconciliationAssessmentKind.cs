namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies the closed conclusion of one authoritative reconciliation assessment.</summary>
public enum GovernedLoopEffectReconciliationAssessmentKind
{
    /// <summary>No supported assessment was supplied.</summary>
    Unknown = 0,

    /// <summary>The retained evidence cannot prove an effect outcome.</summary>
    Inconclusive = 1,

    /// <summary>At least two exact authoritative observations contradict one another.</summary>
    Conflicting = 2,

    /// <summary>Fresh authoritative evidence proves that the effect was not applied.</summary>
    ProvedNotApplied = 3,

    /// <summary>Fresh authoritative evidence proves that the effect was applied and succeeded.</summary>
    ProvedAppliedSucceeded = 4,

    /// <summary>Fresh authoritative evidence proves that the effect was applied and failed.</summary>
    ProvedAppliedFailed = 5,

    /// <summary>Fresh authoritative evidence proves application but not its resulting outcome.</summary>
    ProvedAppliedOutcomeUnknown = 6
}
