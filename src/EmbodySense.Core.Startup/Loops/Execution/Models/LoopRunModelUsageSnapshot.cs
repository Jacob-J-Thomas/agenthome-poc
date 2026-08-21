namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects authenticated reservation and provider-usage evidence for one durable run.</summary>
public sealed record LoopRunModelUsageSnapshot(
    string Status,
    long WorkspaceLedgerGeneration,
    IReadOnlyList<LoopRunModelUsageAttemptSnapshot> Attempts,
    LoopRunModelUsageAggregateSnapshot? Run,
    IReadOnlyList<LoopRunModelUsageAggregateSnapshot> NodeSeries);
