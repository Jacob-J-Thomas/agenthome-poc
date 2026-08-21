namespace EmbodySense.Core.Common.Loops.Execution.Retry;

/// <summary>Defines finite schema-1 retry policy, evidence, and resource bounds.</summary>
public static class GovernedLoopRetryContractLimits
{
    /// <summary>Gets the maximum total attempts, including the original attempt.</summary>
    public const int MaximumAttempts = 8;
    /// <summary>Gets the maximum per-attempt timeout in milliseconds.</summary>
    public const long MaximumPerAttemptTimeoutMilliseconds = 15 * 60 * 1000;
    /// <summary>Gets the maximum cumulative series duration in milliseconds.</summary>
    public const long MaximumElapsedMilliseconds = 30L * 24 * 60 * 60 * 1000;
    /// <summary>Gets the maximum individual delay or deterministic jitter bound in milliseconds.</summary>
    public const long MaximumDelayMilliseconds = 7L * 24 * 60 * 60 * 1000;
    /// <summary>Gets the maximum number of admitted canonical failure classes.</summary>
    public const int MaximumFailureClasses = 8;
    /// <summary>Gets the maximum number of admitted server codes.</summary>
    public const int MaximumServerCodes = 16;
    /// <summary>Gets the maximum authoritative cumulative token ceiling.</summary>
    public const long MaximumTokens = 1_000_000_000;
    /// <summary>Gets the maximum authoritative cumulative tool-attempt ceiling.</summary>
    public const int MaximumToolCalls = 1_024;
    /// <summary>Gets the maximum authoritative cumulative cost ceiling in server-owned micro-units.</summary>
    public const long MaximumCostMicrounits = 1_000_000_000_000;
    /// <summary>Gets the maximum authoritative cumulative catalog resource-unit ceiling.</summary>
    public const int MaximumResourceUnits = 1_024;
}
