namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects monotonic retry attempt and authoritative-or-unknown cumulative usage totals.</summary>
public sealed record LoopRunRetryBudgetSnapshot(
    int Attempts,
    long? Tokens,
    int? ToolCalls,
    long? CostMicrounits,
    string? CostCurrency,
    int? ResourceUnits);
