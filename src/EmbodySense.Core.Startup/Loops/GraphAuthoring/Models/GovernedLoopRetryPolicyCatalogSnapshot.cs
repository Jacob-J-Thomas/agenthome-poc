namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Describes the closed server-owned retry-policy authoring vocabulary and absolute schema-1 bounds.</summary>
public sealed record GovernedLoopRetryPolicyCatalogSnapshot(
    IReadOnlyList<string> FailureClasses,
    IReadOnlyList<string> BackoffStrategies,
    IReadOnlyList<string> JitterStrategies,
    int MaximumAttempts,
    long MaximumPerAttemptTimeoutMilliseconds,
    long MaximumElapsedMilliseconds,
    long MaximumDelayMilliseconds,
    int MaximumServerCodes,
    long MaximumTokens,
    int MaximumToolCalls,
    long MaximumCostMicrounits,
    int MaximumResourceUnits);
