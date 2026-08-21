using EmbodySense.Core.Common.Loops.Execution.Retry.Models;

namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Captures fresh value-free retry eligibility and authoritative cumulative usage.</summary>
/// <param name="LifecycleEligible">Whether the exact run is active, unpaused, and uncancelled.</param>
/// <param name="AuthorityEligible">Whether current exact authority still admits the node.</param>
/// <param name="DependenciesEligible">Whether current exact capability and provider dependencies remain ready.</param>
/// <param name="Budget">The authoritative or explicitly unavailable cumulative budget after the failed attempt.</param>
/// <param name="ObservedAtUtc">The trusted UTC completion time of the current read.</param>
public sealed record GovernedLoopRetryCurrentPosture(
    bool LifecycleEligible,
    bool AuthorityEligible,
    bool DependenciesEligible,
    GovernedLoopRetryBudgetSnapshot Budget,
    DateTimeOffset ObservedAtUtc);
