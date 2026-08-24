namespace EmbodySense.Core.Common.Loops.Execution.Retry.Models;

/// <summary>Retains monotonic authoritative or conservatively reserved resource totals for one retry series.</summary>
/// <param name="Attempts">The positive number of consumed or reserved attempts.</param>
/// <param name="Tokens">The authoritative cumulative token total, or <see langword="null"/> when unavailable.</param>
/// <param name="ToolCalls">The authoritative cumulative tool-attempt total, or <see langword="null"/> when unavailable.</param>
/// <param name="CostMicrounits">The authoritative cumulative cost total, or <see langword="null"/> when unavailable.</param>
/// <param name="CostCurrency">The exact currency paired with authoritative cost, or <see langword="null"/> when cost is unavailable.</param>
/// <param name="ResourceUnits">The authoritative cumulative catalog resource units, or <see langword="null"/> when unavailable.</param>
public sealed record GovernedLoopRetryBudgetSnapshot(
    int Attempts,
    long? Tokens,
    int? ToolCalls,
    long? CostMicrounits,
    string? CostCurrency,
    int? ResourceUnits);
