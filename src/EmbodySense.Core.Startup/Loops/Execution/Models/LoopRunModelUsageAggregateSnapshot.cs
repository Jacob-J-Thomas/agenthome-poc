namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Aggregates only compatible authoritative usage while retaining unavailable and outstanding posture.</summary>
public sealed record LoopRunModelUsageAggregateSnapshot(
    string Scope,
    string? NodeId,
    int AttemptCount,
    int UsageUnavailableAttemptCount,
    int UsageUnknownAttemptCount,
    int OutstandingReservationAttemptCount,
    LoopRunModelUsageDimensionAggregateSnapshot InputTokens,
    LoopRunModelUsageDimensionAggregateSnapshot OutputTokens,
    LoopRunModelUsageDimensionAggregateSnapshot CachedTokens,
    LoopRunModelUsageDimensionAggregateSnapshot TotalTokens,
    IReadOnlyList<LoopRunModelMonetaryCurrencyAggregateSnapshot> MonetaryCosts);
