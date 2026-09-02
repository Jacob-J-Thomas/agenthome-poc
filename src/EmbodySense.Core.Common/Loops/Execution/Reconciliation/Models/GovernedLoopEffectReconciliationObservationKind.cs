namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Classifies how one value-free reconciliation observation completed.</summary>
public enum GovernedLoopEffectReconciliationObservationKind
{
    /// <summary>No supported observation kind was supplied.</summary>
    Unknown = 0,

    /// <summary>A registered source returned exact hash-bound evidence.</summary>
    Evidence = 1,

    /// <summary>The source reported that required evidence was absent.</summary>
    Missing = 2,

    /// <summary>The source observation exceeded its bounded deadline.</summary>
    TimedOut = 3,

    /// <summary>The source observation was cancelled before producing evidence.</summary>
    Cancelled = 4,

    /// <summary>Only unstructured prose was available.</summary>
    Prose = 5,

    /// <summary>Only an assertion from the effect caller was available.</summary>
    CallerAssertion = 6,

    /// <summary>An evidence reference was supplied without a verified exact hash.</summary>
    UnprovenHash = 7
}
