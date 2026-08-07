namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns the closed outcome of one optimistic queue cancellation.</summary>
/// <param name="Status">The cancellation outcome.</param>
/// <param name="Entry">The matching entry when available.</param>
public sealed record TriggerQueueCancellationResult(TriggerQueueCancellationStatus Status, TriggerQueueEntry? Entry);
