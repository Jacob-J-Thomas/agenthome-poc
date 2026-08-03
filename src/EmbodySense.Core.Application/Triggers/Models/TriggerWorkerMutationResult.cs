namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns one closed worker-state mutation outcome.</summary>
/// <param name="Status">The mutation status.</param>
/// <param name="QueueGeneration">The observed or committed queue generation.</param>
/// <param name="Entry">The latest entry projection when available.</param>
public sealed record TriggerWorkerMutationResult(TriggerWorkerMutationStatus Status, long QueueGeneration, TriggerQueueEntry? Entry);
