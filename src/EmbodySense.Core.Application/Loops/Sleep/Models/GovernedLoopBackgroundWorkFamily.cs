namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Names one independently paged durable background-work family.</summary>
public enum GovernedLoopBackgroundWorkFamily
{
    /// <summary>Revision-pinned schedule evaluation candidates.</summary>
    Schedule = 0,
    /// <summary>Unclaimed sleeping-checkpoint wake candidates.</summary>
    Wake = 1,
    /// <summary>Prepared or ambiguous wake-reconciliation candidates.</summary>
    WakeReconciliation = 2
}
