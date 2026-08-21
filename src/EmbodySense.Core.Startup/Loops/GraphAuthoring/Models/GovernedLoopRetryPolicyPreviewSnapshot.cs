namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Projects finite worst-case policy reach without granting execution or choosing a trusted failure.</summary>
public sealed record GovernedLoopRetryPolicyPreviewSnapshot(
    int MaximumAttempts,
    int MaximumRetries,
    long MaximumBackoffMilliseconds,
    long MaximumAttemptExecutionMilliseconds,
    long MaximumReachableElapsedMilliseconds,
    long? MaximumTokens,
    int? MaximumToolCalls,
    long? MaximumCostMicrounits,
    string? MaximumCostCurrency,
    int? MaximumResourceUnits,
    bool CurrentAdmissionStillRequired);
