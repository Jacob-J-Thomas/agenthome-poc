namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies the registered source contract without granting authority through prose.</summary>
public enum GovernedLoopEffectReconciliationEvidenceSourceKind
{
    /// <summary>No supported source kind was supplied.</summary>
    Unknown = 0,

    /// <summary>The registered source is authoritative for exact external-effect observations.</summary>
    Authoritative = 1,

    /// <summary>The registered source is informational and cannot prove an effect outcome.</summary>
    Informational = 2
}
