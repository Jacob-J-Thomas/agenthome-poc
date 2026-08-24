namespace EmbodySense.Core.Application.Loops.Failures.Models;

/// <summary>Identifies whether classification produced a routable failure or an integrity-safe review stop.</summary>
public enum GovernedLoopFailureClassificationStatus
{
    /// <summary>The input was malformed and no trusted evidence was produced.</summary>
    Invalid = 0,
    /// <summary>A safely known failure was classified.</summary>
    Classified,
    /// <summary>Ambiguity or integrity loss requires durable review and cannot route through Failure.</summary>
    ReviewBlocked,
}
