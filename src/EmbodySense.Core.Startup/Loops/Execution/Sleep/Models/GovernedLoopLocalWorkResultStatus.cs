namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Classifies one bounded local background-work attempt without replacing subsystem evidence.</summary>
public enum GovernedLoopLocalWorkResultStatus
{
    /// <summary>One candidate reached its subsystem-owned safe boundary.</summary>
    Completed = 1,

    /// <summary>No candidate is currently eligible in this family.</summary>
    Empty = 2,

    /// <summary>One bounded capacity gate refused more work for this family.</summary>
    Backpressured = 3,

    /// <summary>Subsystem evidence requires attention or a later explicit reconciliation.</summary>
    AttentionRequired = 4,

    /// <summary>An optimistic race prevented this one-shot attempt.</summary>
    Conflict = 5,

    /// <summary>A required durable dependency was conclusively unavailable.</summary>
    Unavailable = 6,

    /// <summary>Retained or returned evidence was malformed or corrupt.</summary>
    Corrupt = 7
}
