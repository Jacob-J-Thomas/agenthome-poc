namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Supplies one exact queue revision plus bounded selection, fairness, and lease inputs.</summary>
/// <param name="WorkerId">The composition-owned worker identity.</param>
/// <param name="ExpectedQueueGeneration">The exact observed queue generation.</param>
/// <param name="ObservedAtUtc">The exact UTC observation instant.</param>
/// <param name="LeaseDuration">The bounded ownership duration.</param>
/// <param name="RecentLoopIds">Newest-last bounded loop-selection history.</param>
/// <param name="MaxConsecutiveSelectionsPerLoop">The bounded same-loop fairness suffix.</param>
public sealed record TriggerWorkerSelectionInput(string WorkerId, long ExpectedQueueGeneration, DateTimeOffset ObservedAtUtc, TimeSpan LeaseDuration, IReadOnlyList<string> RecentLoopIds, int MaxConsecutiveSelectionsPerLoop);
