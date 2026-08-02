namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Requests deterministic generation-bound selection from one exact queue revision.</summary>
/// <param name="WorkerId">The composition-owned worker identity.</param>
/// <param name="ExpectedQueueGeneration">The exact queue generation observed by the caller.</param>
/// <param name="ObservedAtUtc">The exact UTC selection instant.</param>
/// <param name="LeaseDuration">The bounded ownership duration.</param>
/// <param name="RecentLoopIds">Bounded newest-last loop identities used only for fairness.</param>
/// <param name="MaxConsecutiveSelectionsPerLoop">The maximum same-loop suffix before a different eligible loop is required.</param>
public sealed record TriggerWorkerSelectionRequest(string WorkerId, long ExpectedQueueGeneration, DateTimeOffset ObservedAtUtc, TimeSpan LeaseDuration, IReadOnlyList<string> RecentLoopIds, int MaxConsecutiveSelectionsPerLoop);
