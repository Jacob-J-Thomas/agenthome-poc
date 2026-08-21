namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Reports bounded all-pending cancellation without hiding partial durable progress.</summary>
public sealed record TriggerQueuePendingCancellationResult(
    TriggerQueuePendingCancellationStatus Status,
    int MatchedCount,
    int AppliedCount,
    int NeedsReviewCount,
    string ReasonCode);
