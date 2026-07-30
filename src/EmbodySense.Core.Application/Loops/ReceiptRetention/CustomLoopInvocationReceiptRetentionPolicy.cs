namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Provides operations for custom loop invocation receipt retention policy.
/// </summary>
public static class CustomLoopInvocationReceiptRetentionPolicy
{
    /// <summary>
    /// Identifies the minimum replay duration custom loop invocation receipt retention policy.
    /// </summary>
    public static readonly TimeSpan MinimumReplayDuration = TimeSpan.FromDays(30);

    /// <summary>
    /// Identifies the operation ownership window custom loop invocation receipt retention policy.
    /// </summary>
    public static readonly TimeSpan OperationOwnershipWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Identifies the stale recovery window custom loop invocation receipt retention policy.
    /// </summary>
    public static readonly TimeSpan StaleRecoveryWindow = OperationOwnershipWindow + TimeSpan.FromSeconds(5);
}
