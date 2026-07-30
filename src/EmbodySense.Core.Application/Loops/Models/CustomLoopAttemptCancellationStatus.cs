namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop attempt cancellation status values.
/// </summary>
public enum CustomLoopAttemptCancellationStatus
{
    /// <summary>
    /// Identifies the unknown custom loop attempt cancellation status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the provider interruption confirmed custom loop attempt cancellation status.
    /// </summary>
    ProviderInterruptionConfirmed = 1,
    /// <summary>
    /// Identifies the signal delivered custom loop attempt cancellation status.
    /// </summary>
    SignalDelivered = 2,
    /// <summary>
    /// Identifies the no active attempt custom loop attempt cancellation status.
    /// </summary>
    NoActiveAttempt = 3,
    /// <summary>
    /// Identifies the owner unavailable custom loop attempt cancellation status.
    /// </summary>
    OwnerUnavailable = 4,
    /// <summary>
    /// Identifies the invalid custom loop attempt cancellation status.
    /// </summary>
    Invalid = 5
}
