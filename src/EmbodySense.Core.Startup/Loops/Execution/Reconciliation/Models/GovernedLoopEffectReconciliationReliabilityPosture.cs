namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies the registered reliability of a redacted source or observation.</summary>
public enum GovernedLoopEffectReconciliationReliabilityPosture
{
    /// <summary>No supported reliability posture was established.</summary>
    Unknown = 0,
    /// <summary>The registered source is authoritative for the exact observed outcome.</summary>
    Authoritative = 1,
    /// <summary>The evidence can corroborate but cannot prove an outcome by itself.</summary>
    Corroborating = 2,
    /// <summary>The observation is explicitly untrusted and cannot prove an outcome.</summary>
    Untrusted = 3,
}
