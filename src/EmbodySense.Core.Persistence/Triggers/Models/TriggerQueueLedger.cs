using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Holds one validated internal schema-version-1 queue ledger and its immediate predecessor content binding.</summary>
internal sealed record TriggerQueueLedger(long Generation, string? PreviousGenerationHash, DateTimeOffset? LastWorkerObservedAtUtc, TriggerQueueQuota Quota, IReadOnlyList<TriggerQueueLedgerEntry> Entries);
