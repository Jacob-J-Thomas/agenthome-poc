namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Identifies one closed command restart probe posture.</summary>
public enum CommandActionReconciliationPosture
{
    /// <summary>No posture was established.</summary>
    Unknown = 0,
    /// <summary>No conclusive retained outcome can be proved after the process-start boundary.</summary>
    Indeterminate = 1,
    /// <summary>One exact conclusive retained outcome was authenticated.</summary>
    OutcomeObserved = 2,
}
