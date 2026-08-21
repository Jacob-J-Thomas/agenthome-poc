namespace EmbodySense.Core.Common.Loops.Execution.Retry.Models;

/// <summary>Identifies the closed deterministic delay strategy for an admitted retry policy.</summary>
public enum GovernedLoopRetryBackoffStrategy
{
    /// <summary>No backoff strategy is defined.</summary>
    Unknown = 0,
    /// <summary>The next retry is eligible without an authored delay.</summary>
    None,
    /// <summary>Every retry uses the same authored delay.</summary>
    Fixed,
    /// <summary>Each retry doubles the prior delay until the authored maximum is reached.</summary>
    Exponential,
}
