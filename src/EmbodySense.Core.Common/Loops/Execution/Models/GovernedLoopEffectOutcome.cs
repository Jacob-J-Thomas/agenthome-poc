namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Classifies the observed external outcome independently from effect phase and evidence completion.</summary>
public enum GovernedLoopEffectOutcome
{
    /// <summary>No external outcome has been observed or inferred.</summary>
    None = 0,
    /// <summary>The irreversible boundary was reached but the external outcome is unknown.</summary>
    OutcomeUnknown,
    /// <summary>Conclusive success was observed.</summary>
    Succeeded,
    /// <summary>Conclusive failure was observed.</summary>
    Failed,
    /// <summary>Available observations conflict.</summary>
    Conflicted,
    /// <summary>Authoritative reconciliation proved that the external effect was not applied.</summary>
    NotApplied
}
