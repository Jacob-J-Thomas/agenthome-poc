namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Aggregates monetary evidence only within one exact currency and never performs conversion.</summary>
public sealed record LoopRunModelMonetaryCurrencyAggregateSnapshot(
    string Currency,
    string Status,
    decimal? AuthoritativeMicros,
    decimal OutstandingBoundedReservationMicros);
