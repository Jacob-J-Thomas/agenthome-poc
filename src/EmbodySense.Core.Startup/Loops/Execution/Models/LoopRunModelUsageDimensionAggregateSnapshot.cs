namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Aggregates one token dimension, omitting a value unless every attempt supplied authoritative evidence.</summary>
public sealed record LoopRunModelUsageDimensionAggregateSnapshot(
    string Status,
    long? AuthoritativeValue,
    long OutstandingBoundedReservation);
