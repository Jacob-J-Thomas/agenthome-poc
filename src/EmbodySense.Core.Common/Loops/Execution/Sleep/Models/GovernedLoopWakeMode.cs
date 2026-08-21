namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Identifies the closed wake condition retained by one sleeping checkpoint.</summary>
public enum GovernedLoopWakeMode
{
    /// <summary>The checkpoint becomes eligible at one exact trusted UTC deadline.</summary>
    Timestamp = 1,

    /// <summary>The checkpoint becomes eligible only from already-authenticated event evidence.</summary>
    AuthenticatedEvent = 2
}
