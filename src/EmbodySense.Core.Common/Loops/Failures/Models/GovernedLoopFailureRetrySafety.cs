namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Describes whether retained evidence proves that a fresh policy may consider retry.</summary>
public enum GovernedLoopFailureRetrySafety
{
    /// <summary>Retry safety is unknown and no automatic retry may be inferred.</summary>
    Unknown = 0,
    /// <summary>The failure is conclusively not retry-safe.</summary>
    NotRetryable,
    /// <summary>No effect occurred, so later policy may consider retry with the exact intent.</summary>
    RetryableWithExactIntent,
}
