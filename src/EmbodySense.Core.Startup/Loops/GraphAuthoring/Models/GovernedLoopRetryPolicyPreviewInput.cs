namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Supplies non-authoritative bounded retry-policy authoring intent for server canonicalization.</summary>
public sealed record GovernedLoopRetryPolicyPreviewInput(
    string PolicyId,
    string NodeId,
    IReadOnlyList<string> FailureClasses,
    IReadOnlyList<string> ServerCodes,
    int MaximumAttempts,
    long PerAttemptTimeoutMilliseconds,
    long MaximumElapsedMilliseconds,
    string BackoffStrategy,
    long InitialDelayMilliseconds,
    long MaximumDelayMilliseconds,
    string JitterStrategy,
    long MaximumJitterMilliseconds,
    long? MaximumTokens,
    int? MaximumToolCalls,
    long? MaximumCostMicrounits,
    string? MaximumCostCurrency,
    int? MaximumResourceUnits);
