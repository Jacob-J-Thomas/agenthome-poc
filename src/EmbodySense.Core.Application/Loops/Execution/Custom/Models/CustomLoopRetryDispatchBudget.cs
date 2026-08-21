namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Supplies the exact remaining retry-series resources that must bound one already-reserved provider attempt.</summary>
/// <param name="RemainingTokens">The positive remaining total-token allowance, or <see langword="null"/> when tokens are not retry-bounded.</param>
/// <param name="RemainingToolCalls">The positive remaining governed-tool-call allowance, or <see langword="null"/> when tools are not retry-bounded.</param>
/// <param name="RemainingCostMicrounits">The positive remaining monetary allowance, or <see langword="null"/> when cost is not retry-bounded.</param>
/// <param name="CostCurrency">The exact currency paired with <paramref name="RemainingCostMicrounits"/>, or <see langword="null"/> when cost is not retry-bounded.</param>
public sealed record CustomLoopRetryDispatchBudget(
    long? RemainingTokens,
    int? RemainingToolCalls,
    long? RemainingCostMicrounits,
    string? CostCurrency);
