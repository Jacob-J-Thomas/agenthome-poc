namespace EmbodySense.Core.Common.Loops.Execution.Retry.Models;

/// <summary>Identifies the closed deterministic jitter strategy for an admitted retry policy.</summary>
public enum GovernedLoopRetryJitterStrategy
{
    /// <summary>No jitter strategy is defined.</summary>
    Unknown = 0,
    /// <summary>No jitter is added.</summary>
    None,
    /// <summary>A stable hash of the retry-series identity and ordinal selects a bounded additive delay.</summary>
    DeterministicBounded,
}
