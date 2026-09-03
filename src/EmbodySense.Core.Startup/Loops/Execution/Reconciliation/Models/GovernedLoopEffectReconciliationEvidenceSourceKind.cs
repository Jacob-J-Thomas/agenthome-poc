namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies a redacted registered reconciliation evidence source.</summary>
public enum GovernedLoopEffectReconciliationEvidenceSourceKind
{
    /// <summary>No supported source kind was established.</summary>
    Unknown = 0,
    /// <summary>The registered source is authoritative for exact external-effect observations.</summary>
    Authoritative = 1,
    /// <summary>The registered source is informational and cannot prove an effect outcome by itself.</summary>
    Informational = 2,
}
