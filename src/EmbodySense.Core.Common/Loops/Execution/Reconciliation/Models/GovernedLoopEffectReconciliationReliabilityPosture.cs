namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies the registered reliability of a reconciliation source or observation.</summary>
public enum GovernedLoopEffectReconciliationReliabilityPosture
{
    /// <summary>No supported reliability posture was supplied.</summary>
    Unknown = 0,

    /// <summary>The registered source contract is authoritative for the exact observed outcome.</summary>
    Authoritative = 1,

    /// <summary>The evidence may corroborate an assessment but cannot prove an outcome by itself.</summary>
    Corroborating = 2,

    /// <summary>The observation is explicitly untrusted and cannot prove an outcome.</summary>
    Untrusted = 3
}
