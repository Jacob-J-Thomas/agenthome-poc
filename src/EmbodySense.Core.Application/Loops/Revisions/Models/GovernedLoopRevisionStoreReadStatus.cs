namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Identifies whether one atomic lifecycle mutation read is trustworthy.</summary>
public enum GovernedLoopRevisionStoreReadStatus
{
    /// <summary>An undefined result that a conforming store never returns.</summary>
    Unknown = 0,
    /// <summary>The global generation and requested graph state were read consistently.</summary>
    Ready = 1,
    /// <summary>The global generation is known and the requested graph has no lifecycle aggregate.</summary>
    NotFound = 2,
    /// <summary>The store could not provide a trustworthy read and published no durable mutation intent.</summary>
    Unavailable = 3,
    /// <summary>Durable evidence could not prove one consistent read outcome.</summary>
    Ambiguous = 4,
}
