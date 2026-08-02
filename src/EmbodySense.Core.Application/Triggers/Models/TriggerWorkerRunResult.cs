namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns one inspectable one-shot worker outcome.</summary>
/// <param name="SelectionStatus">The selection outcome.</param>
/// <param name="MutationStatus">The final durable mutation outcome when selection succeeded.</param>
/// <param name="Entry">The latest durable entry projection.</param>
public sealed record TriggerWorkerRunResult(TriggerWorkerSelectionStatus SelectionStatus, TriggerWorkerMutationStatus? MutationStatus, TriggerQueueEntry? Entry);
