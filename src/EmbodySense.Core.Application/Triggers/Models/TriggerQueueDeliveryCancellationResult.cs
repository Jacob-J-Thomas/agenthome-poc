namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Reports one delivery cancellation and detached authoritative entry evidence when available.</summary>
public sealed record TriggerQueueDeliveryCancellationResult(TriggerQueueDeliveryCancellationStatus Status, TriggerQueueEntry? Entry, string ReasonCode);
