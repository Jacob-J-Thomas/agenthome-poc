using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Retry.Models;

/// <summary>Defines one immutable bounded opt-in retry policy for an exact graph node.</summary>
/// <param name="SchemaVersion">The policy schema version, which must be 1.</param>
/// <param name="PolicyId">The authored stable policy identity.</param>
/// <param name="NodeId">The exact graph-node scope.</param>
/// <param name="FailureClasses">The sorted canonical failure classes admitted by policy.</param>
/// <param name="ServerCodes">The optional sorted server-code narrowing; empty admits every code in an admitted class.</param>
/// <param name="MaximumAttempts">The maximum total attempts including the original attempt.</param>
/// <param name="PerAttemptTimeoutMilliseconds">The immutable timeout for each fresh attempt.</param>
/// <param name="MaximumElapsedMilliseconds">The cumulative immutable node deadline measured from the retry-series start.</param>
/// <param name="BackoffStrategy">The closed deterministic backoff strategy.</param>
/// <param name="InitialDelayMilliseconds">The fixed delay or first exponential delay.</param>
/// <param name="MaximumDelayMilliseconds">The inclusive delay cap.</param>
/// <param name="JitterStrategy">The closed deterministic jitter strategy.</param>
/// <param name="MaximumJitterMilliseconds">The inclusive deterministic additive jitter bound.</param>
/// <param name="MaximumTokens">The optional cumulative authoritative token ceiling.</param>
/// <param name="MaximumToolCalls">The optional cumulative authoritative tool-attempt ceiling.</param>
/// <param name="MaximumCostMicrounits">The optional cumulative authoritative cost ceiling in one server-owned micro-unit.</param>
/// <param name="MaximumCostCurrency">The exact uppercase ISO-4217 currency paired with the cost ceiling, or null when cost is unbounded.</param>
/// <param name="MaximumResourceUnits">The optional cumulative catalog resource-unit ceiling.</param>
/// <param name="ContentHash">The canonical lowercase SHA-256 digest over every preceding field.</param>
public sealed record GovernedLoopRetryPolicy(
    int SchemaVersion,
    string PolicyId,
    string NodeId,
    IReadOnlyList<GovernedLoopFailureClass> FailureClasses,
    IReadOnlyList<string> ServerCodes,
    int MaximumAttempts,
    long PerAttemptTimeoutMilliseconds,
    long MaximumElapsedMilliseconds,
    GovernedLoopRetryBackoffStrategy BackoffStrategy,
    long InitialDelayMilliseconds,
    long MaximumDelayMilliseconds,
    GovernedLoopRetryJitterStrategy JitterStrategy,
    long MaximumJitterMilliseconds,
    long? MaximumTokens,
    int? MaximumToolCalls,
    long? MaximumCostMicrounits,
    string? MaximumCostCurrency,
    int? MaximumResourceUnits,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental policy schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
