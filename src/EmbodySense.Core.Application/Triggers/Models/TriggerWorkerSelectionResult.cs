using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns selected envelope and exact durable ownership evidence.</summary>
/// <param name="Status">The closed selection outcome.</param>
/// <param name="QueueGeneration">The observed or committed queue generation.</param>
/// <param name="Entry">The selected or conflicting entry projection when available.</param>
/// <param name="Envelope">The selected canonical envelope when ownership was acquired.</param>
public sealed record TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus Status, long QueueGeneration, TriggerQueueEntry? Entry, TriggerDeliveryEnvelope? Envelope);
